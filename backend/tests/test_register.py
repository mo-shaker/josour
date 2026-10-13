"""Self-registration and approval (ADR-0017)."""

from typing import Any

from httpx import ASGITransport, AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.security import verify_password_async
from app.models import SecurityEvent, User
from app.models.enums import ApprovalStatus, RegisteredVia, SecurityEventType
from tests.conftest import PASSWORD, AppFactory, LoginBody, make_settings

REGISTER = "/api/v1/auth/register"
LOGIN = "/api/v1/auth/login"
USERS = "/api/v1/admin/users"
SETTINGS = "/api/v1/admin/settings"


def _body(email: str = "newbie@example.com", **overrides: Any) -> dict[str, Any]:
    return {"email": email, "password": PASSWORD, "display_name": "Newbie", **overrides}


async def _set_mode(client: AsyncClient, admin_headers: dict, mode: str) -> None:
    response = await client.patch(SETTINGS, json={"registration_mode": mode}, headers=admin_headers)
    assert response.status_code == 200, response.text


async def test_registration_mode_is_public_and_defaults_to_approval(client: AsyncClient) -> None:
    response = await client.get("/api/v1/auth/registration")
    assert response.status_code == 200
    assert response.json() == {"mode": "approval"}


async def test_an_approval_mode_registration_is_pending_and_cannot_sign_in(
    client: AsyncClient, db: AsyncSession, login_body: LoginBody
) -> None:
    response = await client.post(REGISTER, json=_body(email="  NewBie@Example.com "))
    assert response.status_code == 202, response.text
    assert response.json() == {"status": "pending"}

    user = await db.scalar(select(User).where(User.email == "newbie@example.com"))
    assert user is not None
    assert (user.approval_status, user.is_active) == (ApprovalStatus.PENDING, False)
    assert user.registered_via == RegisteredVia.SELF
    assert user.state == "pending"

    # The right password learns the account is waiting; a wrong one learns nothing.
    pending = await client.post(LOGIN, json=login_body(email="newbie@example.com"))
    assert pending.status_code == 403
    assert pending.json()["error"]["code"] == "account_pending"
    wrong = await client.post(
        LOGIN, json=login_body(email="newbie@example.com", password="wrong-password")
    )
    assert wrong.status_code == 401
    assert wrong.json()["error"]["code"] == "invalid_credentials"


async def test_a_taken_email_gets_exactly_the_same_answer_in_approval_mode(
    client: AsyncClient, user: User, db: AsyncSession
) -> None:
    """Registering must not tell anyone which addresses have an account."""
    fresh = await client.post(REGISTER, json=_body(email="someone-new@example.com"))
    taken = await client.post(REGISTER, json=_body(email=user.email, password="another-password"))
    assert (taken.status_code, taken.content) == (fresh.status_code, fresh.content)

    # Nothing about the existing account changed, and the attempt is on the record.
    await db.refresh(user)
    assert await verify_password_async(user.password_hash, PASSWORD)
    assert user.approval_status == ApprovalStatus.APPROVED
    duplicate = await db.scalar(
        select(SecurityEvent).where(SecurityEvent.type == SecurityEventType.REGISTRATION_DUPLICATE)
    )
    assert duplicate is not None and duplicate.user_id == user.id


async def test_approval_lets_the_account_in(
    client: AsyncClient, admin_headers: dict, db: AsyncSession, login_body: LoginBody
) -> None:
    await client.post(REGISTER, json=_body())
    queue = (await client.get(USERS, params={"state": "pending"}, headers=admin_headers)).json()
    assert [u["email"] for u in queue] == ["newbie@example.com"]
    user_id = queue[0]["id"]

    approved = await client.post(f"{USERS}/{user_id}/approve", headers=admin_headers)
    assert approved.status_code == 200, approved.text
    assert approved.json()["state"] == "active"
    assert approved.json()["approval_status"] == "approved"

    signed_in = await client.post(LOGIN, json=login_body(email="newbie@example.com"))
    assert signed_in.status_code == 200, signed_in.text

    again = await client.post(f"{USERS}/{user_id}/approve", headers=admin_headers)
    assert again.status_code == 409


async def test_rejection_keeps_the_account_out_and_can_be_reversed(
    client: AsyncClient, admin_headers: dict, login_body: LoginBody
) -> None:
    await client.post(REGISTER, json=_body())
    user_id = (await client.get(USERS, params={"state": "pending"}, headers=admin_headers)).json()[
        0
    ]["id"]

    rejected = await client.post(f"{USERS}/{user_id}/reject", headers=admin_headers)
    assert rejected.status_code == 200 and rejected.json()["state"] == "rejected"
    refused = await client.post(LOGIN, json=login_body(email="newbie@example.com"))
    assert refused.status_code == 403
    assert refused.json()["error"]["code"] == "account_rejected"

    # Rejecting twice is a conflict; approving after all is allowed.
    assert (
        await client.post(f"{USERS}/{user_id}/reject", headers=admin_headers)
    ).status_code == 409
    assert (
        await client.post(f"{USERS}/{user_id}/approve", headers=admin_headers)
    ).status_code == 200


async def test_a_pending_account_cannot_be_activated_around_approval(
    client: AsyncClient, admin_headers: dict
) -> None:
    await client.post(REGISTER, json=_body())
    user_id = (await client.get(USERS, params={"state": "pending"}, headers=admin_headers)).json()[
        0
    ]["id"]
    response = await client.patch(
        f"{USERS}/{user_id}", json={"is_active": True}, headers=admin_headers
    )
    assert response.status_code == 409


async def test_open_mode_creates_an_active_account_and_says_when_the_email_is_taken(
    client: AsyncClient, admin_headers: dict, login_body: LoginBody, user: User
) -> None:
    await _set_mode(client, admin_headers, "open")
    created = await client.post(REGISTER, json=_body())
    assert created.status_code == 201 and created.json() == {"status": "active"}
    assert (
        await client.post(LOGIN, json=login_body(email="newbie@example.com"))
    ).status_code == 200

    taken = await client.post(REGISTER, json=_body(email=user.email))
    assert taken.status_code == 409


async def test_closed_mode_refuses_registration(client: AsyncClient, admin_headers: dict) -> None:
    await _set_mode(client, admin_headers, "closed")
    response = await client.post(REGISTER, json=_body())
    assert response.status_code == 403
    assert response.json()["error"]["code"] == "registration_closed"
    assert (await client.get("/api/v1/auth/registration")).json() == {"mode": "closed"}


async def test_registration_validates_its_input(client: AsyncClient) -> None:
    for body in (
        _body(email="not-an-email"),
        _body(password="short"),
        _body(display_name="   "),
    ):
        assert (await client.post(REGISTER, json=body)).status_code == 422, body


async def test_too_many_pending_registrations_close_the_door(app_factory: AppFactory) -> None:
    app = app_factory(make_settings(max_pending_registrations=1))
    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://test") as client:
        assert (await client.post(REGISTER, json=_body(email="one@example.com"))).status_code == 202
        second = await client.post(REGISTER, json=_body(email="two@example.com"))
    assert second.status_code == 429
    assert second.json()["error"]["code"] == "registration_unavailable"


async def test_registration_is_rate_limited_per_ip(app_factory: AppFactory) -> None:
    app = app_factory(make_settings(rate_limit_enabled=True, register_rate_limit_per_hour=2))
    async with AsyncClient(transport=ASGITransport(app=app), base_url="http://test") as client:
        codes = [
            (await client.post(REGISTER, json=_body(email=f"r{i}@example.com"))).status_code
            for i in range(3)
        ]
    assert codes == [202, 202, 429]


async def test_an_outdated_app_is_told_to_upgrade(
    client: AsyncClient, admin_headers: dict, user: User, login_body: LoginBody
) -> None:
    patched = await client.patch(
        SETTINGS, json={"min_client_version": "2.0"}, headers=admin_headers
    )
    assert patched.status_code == 200, patched.text

    old = await client.post(LOGIN, json=login_body(), headers={"User-Agent": "Josour/1.4.2"})
    assert old.status_code == 426
    assert old.json()["error"]["code"] == "upgrade_required"
    current = await client.post(LOGIN, json=login_body(), headers={"User-Agent": "Josour/2.0.0"})
    assert current.status_code == 200
    # Anything that is not the app (an operator's curl) is not steered.
    assert (await client.post(LOGIN, json=login_body())).status_code == 200

    bad = await client.patch(SETTINGS, json={"min_client_version": "two"}, headers=admin_headers)
    assert bad.status_code == 422
