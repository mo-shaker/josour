"""Verified registration and password reset (ADR-0019)."""

import re
from datetime import timedelta
from typing import Any

import pytest
from fastapi import FastAPI
from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.models import PendingSignup, SecurityEvent, User
from app.models.enums import RegisteredVia, RegistrationMode, SecurityEventType, WorkspaceMode
from app.schemas.settings import SettingsPatch
from app.services.app_settings import settings_service
from app.services.outbox import MemoryOutbox, Senders
from tests.conftest import PASSWORD, LoginBody

REGISTER = "/api/v1/auth/register"
CONFIRM = "/api/v1/auth/register/confirm"
RESEND = "/api/v1/auth/register/resend"
PHONE = "/api/v1/auth/register/phone"
PHONE_CONFIRM = "/api/v1/auth/register/phone/confirm"
FORGOT = "/api/v1/auth/password/forgot"
RESET = "/api/v1/auth/password/reset"
LOGIN = "/api/v1/auth/login"
EMAIL = "newbie@example.com"
NUMBER = "+201001234567"


@pytest.fixture
def mail(app: FastAPI) -> MemoryOutbox:
    return _senders(app).mail  # type: ignore[return-value]


@pytest.fixture
def sms(app: FastAPI) -> MemoryOutbox:
    return _senders(app).sms  # type: ignore[return-value]


def _senders(app: FastAPI) -> Senders:
    senders = getattr(app.state, "senders", None)
    if not isinstance(senders, Senders) or not isinstance(senders.mail, MemoryOutbox):
        senders = Senders(mail=MemoryOutbox(), sms=MemoryOutbox())
        app.state.senders = senders
    return senders


async def _mode(db: AsyncSession, **changes: Any) -> None:
    await settings_service.update(
        db, SettingsPatch(registration_mode=RegistrationMode.VERIFY, **changes)
    )


@pytest.fixture
async def verify(db: AsyncSession, mail: MemoryOutbox, sms: MemoryOutbox) -> None:
    await _mode(db)


def _code(outbox: MemoryOutbox, to: str) -> str:
    message = outbox.last_to(to)
    assert message is not None, f"nothing was sent to {to}"
    found = re.search(r"\b(\d{6})\b", message.body)
    assert found is not None, message.body
    return found.group(1)


def _wrong(code: str) -> str:
    return f"{(int(code) + 1) % 1_000_000:06d}"


def _body(email: str = EMAIL, **overrides: Any) -> dict[str, Any]:
    return {"email": email, "password": PASSWORD, "display_name": "Newbie", **overrides}


# ---------------------------------------------------------------- email only


async def test_verify_mode_creates_the_account_only_after_the_code(
    client: AsyncClient, db: AsyncSession, verify: None, mail: MemoryOutbox, login_body: LoginBody
) -> None:
    info = (await client.get("/api/v1/auth/registration")).json()
    assert info == {"mode": "verify", "phone_required": False, "password_reset": True}

    response = await client.post(REGISTER, json=_body(email=" NewBie@Example.com "))
    assert (response.status_code, response.json()) == (202, {"status": "verify"})
    assert await db.scalar(select(User).where(User.email == EMAIL)) is None
    # No account yet, so signing in says what it says for any wrong password.
    early = await client.post(LOGIN, json=login_body(email=EMAIL))
    assert early.json()["error"]["code"] == "invalid_credentials"

    code = _code(mail, EMAIL)
    wrong = await client.post(CONFIRM, json={"email": EMAIL, "code": _wrong(code)})
    assert (wrong.status_code, wrong.json()["error"]["code"]) == (400, "invalid_code")
    confirmed = await client.post(CONFIRM, json={"email": EMAIL, "code": code})
    assert confirmed.status_code == 201, confirmed.text
    assert confirmed.json() == {"status": "active", "signup_token": None}

    user = await db.scalar(select(User).where(User.email == EMAIL))
    assert user is not None and user.is_active and user.email_verified_at is not None
    assert user.registered_via == RegisteredVia.SELF and user.phone is None
    assert await db.scalar(select(PendingSignup)) is None
    assert (await client.post(LOGIN, json=login_body(email=EMAIL))).status_code == 200
    # The code is spent.
    again = await client.post(CONFIRM, json={"email": EMAIL, "code": code})
    assert again.json()["error"]["code"] == "invalid_code"


async def test_a_taken_address_looks_exactly_like_a_free_one(
    client: AsyncClient,
    db: AsyncSession,
    verify: None,
    mail: MemoryOutbox,
    user: User,
    login_body: LoginBody,
) -> None:
    fresh = await client.post(REGISTER, json=_body(email="someone-new@example.com"))
    taken = await client.post(REGISTER, json=_body(email=user.email, password="attacker-pass"))
    assert (taken.status_code, taken.content) == (fresh.status_code, fresh.content)

    # The owner is told; no code goes out; nothing about the account changed.
    notice = mail.last_to(user.email)
    assert notice is not None and re.search(r"\b\d{6}\b", notice.body) is None
    login = await client.post(LOGIN, json=login_body(email=user.email, password="attacker-pass"))
    assert login.json()["error"]["code"] == "invalid_credentials"
    guess = await client.post(CONFIRM, json={"email": user.email, "code": "123456"})
    assert guess.json()["error"]["code"] == "invalid_code"
    assert (await client.post(LOGIN, json=login_body(email=user.email))).status_code == 200

    # The notice is not repeated for every attempt.
    sent = len(mail.sent)
    await client.post(REGISTER, json=_body(email=user.email))
    assert len(mail.sent) == sent
    duplicates = list(
        await db.scalars(
            select(SecurityEvent).where(
                SecurityEvent.type == SecurityEventType.REGISTRATION_DUPLICATE
            )
        )
    )
    assert len(duplicates) == 2


async def test_five_wrong_codes_kill_the_code(
    client: AsyncClient, verify: None, mail: MemoryOutbox, db: AsyncSession
) -> None:
    await client.post(REGISTER, json=_body())
    code = _code(mail, EMAIL)
    for _ in range(5):
        await client.post(CONFIRM, json={"email": EMAIL, "code": _wrong(code)})
    late = await client.post(CONFIRM, json={"email": EMAIL, "code": code})
    assert late.json()["error"]["code"] == "invalid_code"

    # A new code (after the cooldown) works again.
    row = await db.scalar(select(PendingSignup))
    assert row is not None
    row.email_code_sent_at = utcnow() - timedelta(minutes=2)
    await db.commit()
    assert (await client.post(RESEND, json={"email": EMAIL})).status_code == 202
    fresh = _code(mail, EMAIL)
    assert (await client.post(CONFIRM, json={"email": EMAIL, "code": fresh})).status_code == 201


async def test_codes_are_not_resent_inside_the_cooldown(
    client: AsyncClient, verify: None, mail: MemoryOutbox
) -> None:
    await client.post(REGISTER, json=_body())
    await client.post(REGISTER, json=_body())
    await client.post(RESEND, json={"email": EMAIL})
    assert len([m for m in mail.sent if m.to == EMAIL]) == 1
    # Registering again inside the cooldown still replaces the password.
    assert (await client.post(RESEND, json={"email": "nobody@example.com"})).status_code == 202


async def test_verify_mode_needs_a_mail_sender(
    client: AsyncClient, db: AsyncSession, app: FastAPI
) -> None:
    app.state.senders = Senders(mail=None, sms=None)
    await _mode(db)
    response = await client.post(REGISTER, json=_body())
    assert (response.status_code, response.json()["error"]["code"]) == (503, "mail_unavailable")
    info = (await client.get("/api/v1/auth/registration")).json()
    assert info["password_reset"] is False


async def test_the_code_steps_close_when_the_mode_changes(
    client: AsyncClient, verify: None, mail: MemoryOutbox, db: AsyncSession
) -> None:
    await client.post(REGISTER, json=_body())
    code = _code(mail, EMAIL)
    await settings_service.update(db, SettingsPatch(registration_mode=RegistrationMode.CLOSED))
    closed = await client.post(CONFIRM, json={"email": EMAIL, "code": code})
    assert (closed.status_code, closed.json()["error"]["code"]) == (403, "registration_closed")


async def test_a_verified_account_gets_its_own_workspace_in_multi_mode(
    client: AsyncClient, db: AsyncSession, mail: MemoryOutbox, sms: MemoryOutbox, user: User
) -> None:
    await _mode(db, workspace_mode=WorkspaceMode.MULTI)
    await client.post(REGISTER, json=_body())
    await client.post(CONFIRM, json={"email": EMAIL, "code": _code(mail, EMAIL)})
    created = await db.scalar(select(User).where(User.email == EMAIL))
    assert created is not None and created.workspace_role == "owner"
    assert created.workspace_id != user.workspace_id


# ---------------------------------------------------------------- with a phone


async def test_a_phone_is_proved_before_the_account_exists(
    client: AsyncClient,
    db: AsyncSession,
    mail: MemoryOutbox,
    sms: MemoryOutbox,
    login_body: LoginBody,
) -> None:
    await _mode(db, require_phone=True)
    assert (await client.get("/api/v1/auth/registration")).json()["phone_required"] is True
    await client.post(REGISTER, json=_body())
    step = await client.post(CONFIRM, json={"email": EMAIL, "code": _code(mail, EMAIL)})
    assert step.status_code == 200 and step.json()["status"] == "phone_required"
    token = step.json()["signup_token"]
    assert await db.scalar(select(User).where(User.email == EMAIL)) is None

    bad = await client.post(PHONE, json={"signup_token": token, "phone": "0100 123 4567"})
    assert bad.status_code == 422
    unknown = await client.post(PHONE, json={"signup_token": "nope", "phone": NUMBER})
    assert unknown.json()["error"]["code"] == "signup_expired"

    sent = await client.post(PHONE, json={"signup_token": token, "phone": "+20 100 123-4567"})
    assert sent.status_code == 202, sent.text
    too_soon = await client.post(PHONE, json={"signup_token": token, "phone": NUMBER})
    assert too_soon.status_code == 429
    code = _code(sms, NUMBER)
    wrong = await client.post(PHONE_CONFIRM, json={"signup_token": token, "code": _wrong(code)})
    assert wrong.json()["error"]["code"] == "invalid_code"
    done = await client.post(PHONE_CONFIRM, json={"signup_token": token, "code": code})
    assert (done.status_code, done.json()["status"]) == (201, "active")

    user = await db.scalar(select(User).where(User.email == EMAIL))
    assert user is not None and user.phone == NUMBER and user.phone_verified_at is not None
    assert (await client.post(LOGIN, json=login_body(email=EMAIL))).status_code == 200

    # One account per number.
    await client.post(REGISTER, json=_body(email="second@example.com"))
    step2 = await client.post(
        CONFIRM, json={"email": "second@example.com", "code": _code(mail, "second@example.com")}
    )
    taken = await client.post(
        PHONE, json={"signup_token": step2.json()["signup_token"], "phone": NUMBER}
    )
    assert (taken.status_code, taken.json()["error"]["code"]) == (409, "phone_in_use")


async def test_require_phone_needs_an_sms_sender(
    client: AsyncClient, db: AsyncSession, app: FastAPI
) -> None:
    app.state.senders = Senders(mail=MemoryOutbox(), sms=None)
    await _mode(db, require_phone=True)
    response = await client.post(REGISTER, json=_body())
    assert (response.status_code, response.json()["error"]["code"]) == (503, "sms_unavailable")


# ---------------------------------------------------------------- password reset


async def test_password_reset_signs_every_device_out(
    client: AsyncClient,
    db: AsyncSession,
    mail: MemoryOutbox,
    user: User,
    logged_in: dict[str, Any],
    login_body: LoginBody,
) -> None:
    nobody = await client.post(FORGOT, json={"email": "nobody@example.com"})
    known = await client.post(FORGOT, json={"email": user.email})
    assert (nobody.status_code, nobody.content) == (known.status_code, known.content) == (202, b"")
    assert mail.last_to("nobody@example.com") is None
    code = _code(mail, user.email)

    short = {"email": user.email, "code": code, "password": "short"}
    assert (await client.post(RESET, json=short)).status_code == 422
    wrong = {"email": user.email, "code": _wrong(code), "password": "a-new-passphrase"}
    assert (await client.post(RESET, json=wrong)).json()["error"]["code"] == "invalid_code"
    right = {"email": user.email, "code": code, "password": "a-new-passphrase"}
    assert (await client.post(RESET, json=right)).status_code == 204

    refresh = await client.post(
        "/api/v1/auth/refresh", json={"refresh_token": logged_in["refresh_token"]}
    )
    assert refresh.status_code == 401
    old = await client.post(LOGIN, json=login_body())
    assert old.json()["error"]["code"] == "invalid_credentials"
    new = await client.post(LOGIN, json=login_body(password="a-new-passphrase"))
    assert new.status_code == 200
    assert (await client.post(RESET, json=right)).json()["error"]["code"] == "invalid_code"
    events = {
        row.type
        for row in await db.scalars(select(SecurityEvent).where(SecurityEvent.user_id == user.id))
    }
    assert {SecurityEventType.PASSWORD_RESET_REQUESTED, SecurityEventType.PASSWORD_RESET} <= events


async def test_password_reset_needs_a_mail_sender(client: AsyncClient, app: FastAPI) -> None:
    app.state.senders = Senders(mail=None, sms=None)
    response = await client.post(FORGOT, json={"email": "alice@example.com"})
    assert (response.status_code, response.json()["error"]["code"]) == (503, "mail_unavailable")


def test_console_senders_are_refused_in_production() -> None:
    from pydantic import ValidationError

    from tests.conftest import make_settings

    with pytest.raises(ValidationError, match="not in prod"):
        make_settings(env="prod", mail_backend="console")
    with pytest.raises(ValidationError, match="SMTP_HOST"):
        make_settings(mail_backend="smtp")
    with pytest.raises(ValidationError, match="TWILIO"):
        make_settings(sms_backend="twilio")
