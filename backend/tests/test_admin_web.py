"""The web dashboard at /admin (ADR-0016)."""

import re
from collections.abc import AsyncIterator
from datetime import timedelta

import pytest
from fastapi import FastAPI
from httpx import ASGITransport, AsyncClient, Response
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core import totp
from app.core.clock import utcnow
from app.models import AdminWebSession, SecurityEvent, Session, User
from app.models.enums import ApprovalStatus, SecurityEventType, SessionStatus
from app.services.users import create_user
from app.web.i18n import STRINGS
from app.web.views import MAX_TOTP_FAILURES
from tests.conftest import (
    ADMIN_EMAIL,
    PASSWORD,
    ActorFactory,
    AppFactory,
    SessionFactory,
    WsFactory,
    make_settings,
)

ORIGIN = "https://test"
_CSRF = re.compile(r'name="csrf" value="([^"]+)"')


@pytest.fixture
async def web(app: FastAPI) -> AsyncIterator[AsyncClient]:
    """A browser: https (so the Secure cookie is kept) and an Origin on every request."""
    async with AsyncClient(
        transport=ASGITransport(app=app), base_url=ORIGIN, headers={"origin": ORIGIN}
    ) as client:
        yield client


async def _sign_in(
    web: AsyncClient, email: str = ADMIN_EMAIL, password: str = PASSWORD
) -> Response:
    return await web.post(
        "/admin/login", data={"email": email, "password": password, "next": "/admin"}
    )


async def _csrf(web: AsyncClient, path: str = "/admin") -> str:
    page = await web.get(path)
    assert page.status_code == 200, page.text
    match = _CSRF.search(page.text)
    assert match is not None
    return match.group(1)


async def _post(
    web: AsyncClient, path: str, data: dict | None = None, *, from_page: str = "/admin"
) -> Response:
    return await web.post(path, data={**(data or {}), "csrf": await _csrf(web, from_page)})


@pytest.fixture
async def signed_in(web: AsyncClient, admin: User) -> AsyncClient:
    response = await _sign_in(web)
    assert response.status_code == 303 and response.headers["location"] == "/admin"
    return web


# ---------------------------------------------------------------- sign-in


async def test_the_dashboard_sends_you_to_sign_in(web: AsyncClient) -> None:
    response = await web.get("/admin/users?state=pending")
    assert response.status_code == 303
    assert response.headers["location"] == "/admin/login?next=%2Fadmin%2Fusers%3Fstate%3Dpending"
    page = await web.get("/admin/login")
    assert page.status_code == 200
    assert 'dir="rtl"' in page.text and STRINGS["login.title"][0] in page.text


async def test_every_page_carries_the_security_headers(web: AsyncClient) -> None:
    page = await web.get("/admin/login")
    assert "script-src" not in page.headers["content-security-policy"]
    assert "default-src 'none'" in page.headers["content-security-policy"]
    assert "frame-ancestors 'none'" in page.headers["content-security-policy"]
    assert page.headers["cache-control"] == "no-store"
    assert page.headers["referrer-policy"] == "same-origin"
    assert page.headers["x-frame-options"] == "DENY"
    css = await web.get("/admin/static/admin.css")
    assert css.status_code == 200 and "text/css" in css.headers["content-type"]


async def test_signing_in_sets_a_locked_down_cookie(
    web: AsyncClient, admin: User, db: AsyncSession
) -> None:
    response = await _sign_in(web)
    assert response.status_code == 303
    cookie = response.headers["set-cookie"]
    assert cookie.startswith("__Host-josour_admin=")
    for flag in ("HttpOnly", "Secure", "SameSite=strict", "Path=/"):
        assert flag.lower() in cookie.lower(), flag
    assert "domain=" not in cookie.lower()
    row = await db.scalar(select(AdminWebSession))
    assert row is not None and row.user_id == admin.id
    # The cookie holds a token; the table holds only its hash.
    token = cookie.split("=", 1)[1].split(";", 1)[0]
    assert token not in (row.token_hash, row.csrf_token)
    assert (await web.get("/admin")).status_code == 200


async def test_a_non_admin_gets_the_same_answer_as_a_wrong_password(
    web: AsyncClient, user: User, admin: User, db: AsyncSession
) -> None:
    not_admin = await _sign_in(web, email=user.email)
    wrong = await _sign_in(web, password="not-the-password")
    assert not_admin.status_code == wrong.status_code == 401
    assert STRINGS["login.failed"][0] in not_admin.text and STRINGS["login.failed"][0] in wrong.text
    assert "set-cookie" not in not_admin.headers
    event = await db.scalar(
        select(SecurityEvent).where(SecurityEvent.type == SecurityEventType.ADMIN_WEB_LOGIN_FAILED)
    )
    assert event is not None and event.details == {"reason": "not_admin"}


async def test_sign_in_needs_our_origin(web: AsyncClient, admin: User) -> None:
    response = await web.post(
        "/admin/login",
        data={"email": ADMIN_EMAIL, "password": PASSWORD},
        headers={"origin": "https://evil.example"},
    )
    assert response.status_code == 403
    assert "set-cookie" not in response.headers


async def test_a_null_origin_is_judged_by_sec_fetch_site(web: AsyncClient, admin: User) -> None:
    """Under ``Referrer-Policy: no-referrer`` (Caddy sets it site-wide) a browser posts our own
    forms with ``Origin: null``; ``Sec-Fetch-Site`` is what tells them apart from a foreign page."""
    body = {"email": ADMIN_EMAIL, "password": PASSWORD}
    ours = await web.post(
        "/admin/login", data=body, headers={"origin": "null", "sec-fetch-site": "same-origin"}
    )
    assert ours.status_code == 303
    web.cookies.clear()
    for headers in (
        {"origin": "null", "sec-fetch-site": "cross-site"},
        {"origin": "null"},
        {"origin": "null", "sec-fetch-site": "same-site"},
    ):
        refused = await web.post("/admin/login", data=body, headers=headers)
        assert refused.status_code == 403, headers


async def test_next_never_leaves_the_dashboard(web: AsyncClient, admin: User) -> None:
    response = await web.post(
        "/admin/login", data={"email": ADMIN_EMAIL, "password": PASSWORD, "next": "//evil.example"}
    )
    assert response.headers["location"] == "/admin"


async def test_signing_out_ends_the_session(signed_in: AsyncClient) -> None:
    response = await _post(signed_in, "/admin/logout")
    assert response.status_code == 303 and response.headers["location"] == "/admin/login"
    signed_in.cookies.clear()
    assert (await signed_in.get("/admin")).status_code == 303


async def test_an_idle_session_expires(signed_in: AsyncClient, db: AsyncSession) -> None:
    row = await db.scalar(select(AdminWebSession))
    assert row is not None
    row.last_seen_at = utcnow() - timedelta(minutes=31)
    await db.commit()
    response = await signed_in.get("/admin")
    assert response.status_code == 303 and response.headers["location"].startswith("/admin/login")


# ---------------------------------------------------------------- forms


async def test_a_form_without_the_csrf_token_is_refused(
    signed_in: AsyncClient, db: AsyncSession
) -> None:
    body = {"email": "new@example.com", "display_name": "New", "password": PASSWORD, "role": "user"}
    missing = await signed_in.post("/admin/users/new", data=body)
    forged = await signed_in.post("/admin/users/new", data={**body, "csrf": "guess"})
    foreign = await signed_in.post(
        "/admin/users/new",
        data={**body, "csrf": await _csrf(signed_in)},
        headers={"origin": "https://evil.example"},
    )
    assert missing.status_code == forged.status_code == foreign.status_code == 403
    assert await db.scalar(select(User).where(User.email == "new@example.com")) is None

    created = await _post(signed_in, "/admin/users/new", body)
    assert created.status_code == 303 and created.headers["location"].endswith("done=created")


async def test_a_registration_is_approved_from_the_queue(
    signed_in: AsyncClient, client: AsyncClient, db: AsyncSession
) -> None:
    await client.post(
        "/api/v1/auth/register",
        json={"email": "newbie@example.com", "password": PASSWORD, "display_name": "Newbie"},
    )
    queue = await signed_in.get("/admin/registrations")
    assert "newbie@example.com" in queue.text
    user = await db.scalar(select(User).where(User.email == "newbie@example.com"))
    assert user is not None

    response = await _post(
        signed_in,
        f"/admin/users/{user.id}/approve",
        {"next": "/admin/registrations"},
        from_page="/admin/registrations",
    )
    assert response.status_code == 303
    assert response.headers["location"] == "/admin/registrations?done=approved"
    await db.refresh(user)
    assert user.approval_status == ApprovalStatus.APPROVED and user.is_active
    event = await db.scalar(
        select(SecurityEvent).where(SecurityEvent.type == SecurityEventType.USER_APPROVED)
    )
    assert event is not None


async def test_deleting_goes_through_a_confirmation(
    signed_in: AsyncClient, user: User, db: AsyncSession
) -> None:
    confirm = await signed_in.get(f"/admin/users/{user.id}/delete")
    assert confirm.status_code == 200
    assert f'action="/admin/users/{user.id}/delete"' in confirm.text

    response = await _post(
        signed_in, f"/admin/users/{user.id}/delete", from_page=f"/admin/users/{user.id}/delete"
    )
    assert response.status_code == 303 and "done=deleted" in response.headers["location"]
    await db.refresh(user)
    assert user.state == "deleted"


async def test_a_refused_action_says_why_instead_of_failing(
    signed_in: AsyncClient, admin: User
) -> None:
    """Deleting yourself is a conflict; the page shows the code rather than a JSON error."""
    response = await _post(signed_in, f"/admin/users/{admin.id}/delete")
    assert response.status_code == 303
    assert response.headers["location"].endswith("error=conflict")
    page = await signed_in.get(response.headers["location"])
    assert "<code>conflict</code>" in page.text


async def test_terminating_a_session_reaches_both_peers(
    signed_in: AsyncClient,
    make_actor: ActorFactory,
    make_session: SessionFactory,
    ws_connect: WsFactory,
    db: AsyncSession,
) -> None:
    guest = await make_actor("guest@example.com")
    host = await make_actor("host@example.com")
    session = await make_session(guest.user, guest.device, host.user, host.device)
    session_id = session.id
    guest_ws, host_ws = await ws_connect(guest), await ws_connect(host)

    listed = await signed_in.get("/admin/sessions?live=1")
    assert f"/admin/sessions/{session_id}/terminate" in listed.text
    response = await _post(signed_in, f"/admin/sessions/{session_id}/terminate")
    assert response.status_code == 303 and "done=terminated" in response.headers["location"]

    for ws in (guest_ws, host_ws):
        frame = await ws.expect("session.terminate")
        assert frame["reason"] == "admin_terminated"
    db.expire_all()
    row = await db.get(Session, session_id)
    assert row is not None and row.status == SessionStatus.ENDED
    event = await db.scalar(
        select(SecurityEvent).where(
            SecurityEvent.type == SecurityEventType.SESSION_ADMIN_TERMINATED
        )
    )
    assert event is not None and event.details["via"] == "web"


async def test_settings_change_registration_mode(
    signed_in: AsyncClient, client: AsyncClient
) -> None:
    response = await _post(
        signed_in,
        "/admin/settings",
        {
            "registration_mode": "closed",
            "max_session_minutes": "90",
            "request_timeout_seconds": "60",
            "connect_timeout_seconds": "30",
            "allowed_ports": "80, 443, 8443",
            "invite_ttl_days": "14",
            "min_client_version": "",
        },
        from_page="/admin/settings",
    )
    assert response.status_code == 303 and "done=saved" in response.headers["location"]
    assert (await client.get("/api/v1/auth/registration")).json() == {"mode": "closed"}
    page = await signed_in.get("/admin/settings")
    assert 'value="80, 443, 8443"' in page.text

    bad = await _post(
        signed_in,
        "/admin/settings",
        {"registration_mode": "closed", "max_session_minutes": "many"},
        from_page="/admin/settings",
    )
    assert bad.headers["location"].endswith("error=validation_error")


async def test_names_are_escaped(signed_in: AsyncClient, db: AsyncSession) -> None:
    hostile = await create_user(
        db, email="x@example.com", password=PASSWORD, display_name="<script>alert(1)</script>"
    )
    await db.commit()
    page = await signed_in.get(f"/admin/users/{hostile.id}")
    assert "<script>alert(1)</script>" not in page.text
    assert "&lt;script&gt;alert(1)&lt;/script&gt;" in page.text


async def test_changing_your_password_signs_every_dashboard_session_out(
    signed_in: AsyncClient,
) -> None:
    response = await _post(
        signed_in,
        "/admin/account/password",
        {"current": PASSWORD, "password": "a-brand-new-password"},
        from_page="/admin/account",
    )
    assert response.headers["location"] == "/admin/login"
    assert (await signed_in.get("/admin")).status_code == 303


# ---------------------------------------------------------------- second factor


async def test_two_step_verification_end_to_end(
    signed_in: AsyncClient, db: AsyncSession, admin: User
) -> None:
    await _post(signed_in, "/admin/account/totp/start", from_page="/admin/account")
    await db.refresh(admin)
    secret = admin.totp_secret
    assert secret is not None and admin.totp_enabled_at is None
    code = totp.code_at(secret, totp.current_step())
    confirmed = await _post(
        signed_in, "/admin/account/totp/confirm", {"code": code}, from_page="/admin/account"
    )
    assert confirmed.headers["location"].endswith("done=saved")
    await db.refresh(admin)
    assert admin.totp_enabled_at is not None

    # Sign out and back in: the password alone now reaches only the code page.
    await _post(signed_in, "/admin/logout")
    signed_in.cookies.clear()
    first = await _sign_in(signed_in)
    assert first.headers["location"] == "/admin/login/totp"
    assert (await signed_in.get("/admin")).headers["location"] == "/admin/login/totp"

    wrong = await _post(
        signed_in, "/admin/login/totp", {"code": "000000"}, from_page="/admin/login/totp"
    )
    assert wrong.headers["location"].endswith("wrong=1")
    # The code that confirmed enrolment has been used: it cannot be replayed.
    replay = await _post(
        signed_in, "/admin/login/totp", {"code": code}, from_page="/admin/login/totp"
    )
    assert replay.headers["location"].endswith("wrong=1")

    fresh = totp.code_at(secret, totp.current_step() + 1)
    passed = await _post(
        signed_in, "/admin/login/totp", {"code": fresh}, from_page="/admin/login/totp"
    )
    assert passed.status_code == 303 and passed.headers["location"] == "/admin"
    assert (await signed_in.get("/admin")).status_code == 200


def test_totp_matches_the_rfc_vectors() -> None:
    """RFC 6238 appendix B, SHA-1, truncated to 6 digits."""
    secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"  # "12345678901234567890"
    assert totp.code_at(secret, 59 // 30) == "287082"
    assert totp.code_at(secret, 1111111109 // 30) == "081804"
    assert totp.verify(secret, "287082", last_step=None, now=59) == 1
    assert totp.verify(secret, "287082", last_step=1, now=59) is None


# ---------------------------------------------------------------- reach


async def test_outside_the_allowed_networks_the_dashboard_does_not_exist(
    app_factory: AppFactory, admin: User
) -> None:
    app = app_factory(make_settings(admin_web_allowed_cidrs="10.0.0.0/8"))
    async with AsyncClient(
        transport=ASGITransport(app=app, client=("203.0.113.5", 1234)),
        base_url=ORIGIN,
        headers={"origin": ORIGIN},
    ) as outside:
        page = await outside.get("/admin/login")
        unknown = await outside.get("/no-such-path")
        # Not the dashboard's own error page: exactly what any unknown path gets, without the
        # dashboard's headers - and the stylesheet is not served either.
        assert page.status_code == unknown.status_code == 404
        assert page.json() == unknown.json()
        assert "content-security-policy" not in page.headers
        assert (await outside.get("/admin/static/admin.css")).status_code == 404
        assert (await outside.get("/admin/users/x/delete")).json() == unknown.json()
    async with AsyncClient(
        transport=ASGITransport(app=app, client=("10.1.2.3", 1234)),
        base_url=ORIGIN,
        headers={"origin": ORIGIN},
    ) as inside:
        assert (await inside.get("/admin/login")).status_code == 200


async def test_the_dashboard_can_be_switched_off(app_factory: AppFactory) -> None:
    app = app_factory(make_settings(admin_web_enabled=False))
    async with AsyncClient(transport=ASGITransport(app=app), base_url=ORIGIN) as c:
        assert (await c.get("/admin/login")).status_code == 404


async def test_language_switch(signed_in: AsyncClient) -> None:
    switched = await signed_in.get("/admin/lang/en?next=/admin/users")
    assert switched.headers["location"] == "/admin/users"
    page = await signed_in.get("/admin/users")
    assert 'lang="en" dir="ltr"' in page.text and "Users" in page.text


def test_every_string_has_both_languages() -> None:
    for key, (arabic, english) in STRINGS.items():
        assert arabic.strip() and english.strip(), key
        assert set(re.findall(r"\{(\w+)\}", arabic)) == set(re.findall(r"\{(\w+)\}", english)), key


# ---------------------------------------------------------------- second factor: limits


async def _with_totp(db: AsyncSession, admin: User) -> str:
    admin.totp_secret = totp.generate_secret()
    admin.totp_enabled_at = utcnow()
    await db.commit()
    return admin.totp_secret


def _wrong_codes(secret: str, count: int) -> list[str]:
    valid = {totp.code_at(secret, totp.current_step() + d) for d in (-1, 0, 1)}
    return [code for code in (f"{i:06d}" for i in range(count + 3)) if code not in valid][:count]


async def _events(db: AsyncSession, event_type: SecurityEventType) -> list[SecurityEvent]:
    db.expire_all()
    return list(await db.scalars(select(SecurityEvent).where(SecurityEvent.type == event_type)))


async def test_five_wrong_codes_end_the_session(
    web: AsyncClient, admin: User, db: AsyncSession
) -> None:
    secret = await _with_totp(db, admin)
    assert (await _sign_in(web)).headers["location"] == "/admin/login/totp"
    csrf = await _csrf(web, "/admin/login/totp")
    answers = [
        await web.post("/admin/login/totp", data={"code": code, "csrf": csrf})
        for code in _wrong_codes(secret, MAX_TOTP_FAILURES)
    ]
    assert all(a.headers["location"].endswith("wrong=1") for a in answers[:-1])
    assert answers[-1].headers["location"] == "/admin/login?reason=totp_limit"
    assert "__Host-josour_admin=" in answers[-1].headers["set-cookie"]  # deleted

    db.expire_all()
    row = await db.scalar(select(AdminWebSession))
    assert row is not None and row.revoked_at is not None and row.totp_failures == 5
    await db.refresh(admin)
    assert admin.failed_logins == MAX_TOTP_FAILURES  # counted like wrong passwords
    # Even the right code is worth nothing now: only the password starts a new session.
    right = await web.post(
        "/admin/login/totp",
        data={"code": totp.code_at(secret, totp.current_step()), "csrf": csrf},
    )
    assert right.headers["location"].startswith("/admin/login?next=")
    page = await web.get("/admin/login?reason=totp_limit")
    assert STRINGS["login.totp_limit"][0] in page.text
    failed = await _events(db, SecurityEventType.ADMIN_WEB_LOGIN_FAILED)
    assert sorted(e.details["failures"] for e in failed) == [1, 2, 3, 4, 5]


async def test_wrong_codes_reach_the_account_lockout(
    web: AsyncClient, admin: User, db: AsyncSession
) -> None:
    """The password alone no longer clears the failure counter when a code must follow, so
    guessing codes across fresh sessions still ends in the lockout."""
    secret = await _with_totp(db, admin)
    admin.failed_logins = 8
    await db.commit()
    assert (await _sign_in(web)).headers["location"] == "/admin/login/totp"
    await db.refresh(admin)
    assert admin.failed_logins == 8
    csrf = await _csrf(web, "/admin/login/totp")
    first, second = _wrong_codes(secret, 2)
    assert (
        (await web.post("/admin/login/totp", data={"code": first, "csrf": csrf}))
        .headers["location"]
        .endswith("wrong=1")
    )
    locked = await web.post("/admin/login/totp", data={"code": second, "csrf": csrf})
    assert locked.headers["location"] == "/admin/login?reason=locked"
    await db.refresh(admin)
    assert admin.locked_until is not None
    assert len(await _events(db, SecurityEventType.LOGIN_LOCKED)) == 1
    again = await _sign_in(web)
    assert again.status_code == 403 and STRINGS["login.locked"][0] in again.text


async def test_the_right_code_clears_the_failure_counter(
    web: AsyncClient, admin: User, db: AsyncSession
) -> None:
    secret = await _with_totp(db, admin)
    await _sign_in(web)
    csrf = await _csrf(web, "/admin/login/totp")
    await web.post("/admin/login/totp", data={"code": _wrong_codes(secret, 1)[0], "csrf": csrf})
    passed = await web.post(
        "/admin/login/totp",
        data={"code": totp.code_at(secret, totp.current_step()), "csrf": csrf},
    )
    assert passed.headers["location"] == "/admin"
    await db.refresh(admin)
    assert admin.failed_logins == 0 and admin.last_login_at is not None


async def test_enrolment_codes_are_limited_and_success_rotates_the_session(
    signed_in: AsyncClient, admin: User, db: AsyncSession
) -> None:
    await _post(signed_in, "/admin/account/totp/start", from_page="/admin/account")
    await db.refresh(admin)
    assert admin.totp_secret is not None
    secret = admin.totp_secret
    wrong = await _post(
        signed_in,
        "/admin/account/totp/confirm",
        {"code": _wrong_codes(secret, 1)[0]},
        from_page="/admin/account",
    )
    assert wrong.headers["location"].endswith("error=totp")
    await db.refresh(admin)
    assert admin.failed_logins == 1

    old_csrf = await _csrf(signed_in, "/admin/account")
    confirmed = await signed_in.post(
        "/admin/account/totp/confirm",
        data={"code": totp.code_at(secret, totp.current_step()), "csrf": old_csrf},
    )
    assert confirmed.headers["location"].endswith("done=saved")
    assert "__Host-josour_admin=" in confirmed.headers["set-cookie"]
    # The session gained a privilege: the CSRF token seen before is worthless now.
    stale = await signed_in.post("/admin/logout", data={"csrf": old_csrf})
    assert stale.status_code == 403
    assert (await signed_in.get("/admin")).status_code == 200
    enabled = await _events(db, SecurityEventType.TOTP_ENABLED)
    assert [e.details for e in enabled] == [{"via": "web"}]


async def test_enrolment_ends_the_session_after_five_wrong_codes(
    signed_in: AsyncClient, admin: User, db: AsyncSession
) -> None:
    await _post(signed_in, "/admin/account/totp/start", from_page="/admin/account")
    await db.refresh(admin)
    assert admin.totp_secret is not None
    csrf = await _csrf(signed_in, "/admin/account")
    answers = [
        await signed_in.post("/admin/account/totp/confirm", data={"code": code, "csrf": csrf})
        for code in _wrong_codes(admin.totp_secret, MAX_TOTP_FAILURES)
    ]
    assert answers[-1].headers["location"] == "/admin/login?reason=totp_limit"
    assert (await signed_in.get("/admin")).headers["location"].startswith("/admin/login")


async def test_requiring_totp_later_reaches_sessions_already_open(
    app_factory: AppFactory, admin: User
) -> None:
    """A password-only session is not "second factor passed": when the operator turns on
    ADMIN_WEB_REQUIRE_TOTP, the same cookie is sent to enrolment on its next page."""
    relaxed = app_factory(None)
    strict = app_factory(make_settings(admin_web_require_totp=True))
    async with AsyncClient(
        transport=ASGITransport(app=relaxed), base_url=ORIGIN, headers={"origin": ORIGIN}
    ) as browser:
        assert (await _sign_in(browser)).headers["location"] == "/admin"
        assert (await browser.get("/admin")).status_code == 200
        cookies = browser.cookies
    async with AsyncClient(
        transport=ASGITransport(app=strict),
        base_url=ORIGIN,
        headers={"origin": ORIGIN},
        cookies=cookies,
    ) as same_browser:
        response = await same_browser.get("/admin")
        assert response.status_code == 303
        assert response.headers["location"] == "/admin/login/totp"


# ---------------------------------------------------------------- audit, lists, confirmations


async def test_dashboard_actions_are_audited_as_web(
    signed_in: AsyncClient, admin: User, user: User, db: AsyncSession
) -> None:
    detail = f"/admin/users/{user.id}"
    await _post(signed_in, f"{detail}/password", {"password": "another-password"}, from_page=detail)
    await _post(signed_in, f"{detail}/deactivate", from_page=detail)
    await _post(signed_in, f"{detail}/activate", from_page=detail)
    created = await _post(
        signed_in,
        "/admin/users/new",
        {
            "email": "second-admin@example.com",
            "password": "long-enough-1",
            "display_name": "Second",
            "role": "admin",
        },
        from_page="/admin/users/new",
    )
    assert created.status_code == 303

    actor = str(admin.id)
    updates = await _events(db, SecurityEventType.USER_UPDATED)
    assert {"password_set": True, "actor_user_id": actor, "via": "web"} in [
        e.details for e in updates
    ]
    assert {"activated": True, "actor_user_id": actor, "via": "web"} in [e.details for e in updates]
    (deactivated,) = await _events(db, SecurityEventType.USER_DEACTIVATED)
    assert deactivated.details["via"] == "web" and deactivated.details["actor_user_id"] == actor
    (made,) = await _events(db, SecurityEventType.USER_CREATED)
    assert made.details == {"role": "admin", "actor_user_id": actor, "via": "web"}


async def test_the_live_view_lists_every_live_session(
    signed_in: AsyncClient, make_actor: ActorFactory, make_session: SessionFactory
) -> None:
    """It used to take the newest 200 sessions and filter them: a live session older than 200
    ended ones disappeared from the page that exists to terminate it."""
    guest = await make_actor("g@example.com")
    host = await make_actor("h@example.com", device_name="HOST-PC")
    live = await make_session(
        guest.user,
        guest.device,
        host.user,
        host.device,
        created_at=utcnow() - timedelta(hours=1),
    )
    for _ in range(201):
        await make_session(
            guest.user, guest.device, host.user, host.device, status=SessionStatus.ENDED
        )
    page = await signed_in.get("/admin/sessions?live=1")
    assert page.status_code == 200 and f"/admin/sessions/{live.id}/terminate" in page.text


async def test_a_huge_page_number_is_not_an_error(signed_in: AsyncClient) -> None:
    assert (await signed_in.get("/admin/users?page=99999999999999999999")).status_code == 200


async def test_a_confirmation_page_needs_a_signed_in_administrator(
    web: AsyncClient, user: User
) -> None:
    response = await web.get(f"/admin/users/{user.id}/delete")
    assert response.status_code == 303
    assert response.headers["location"].startswith("/admin/login?next=")
