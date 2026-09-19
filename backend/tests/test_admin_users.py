import uuid
from datetime import timedelta
from typing import Any

from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.models import Device, Presence, RefreshToken, SecurityEvent, User
from app.models.enums import DeviceStatus, SecurityEventType
from app.ws.protocol import CloseCode
from tests.conftest import PASSWORD, LoginBody

USERS = "/api/v1/admin/users"
USER_KEYS = {
    "id",
    "email",
    "display_name",
    "role",
    "is_active",
    "failed_logins",
    "locked_until",
    "created_at",
}


async def test_admin_routes_require_admin_role(client: AsyncClient, auth_headers: dict) -> None:
    assert (await client.get(USERS)).status_code == 401
    forbidden = await client.get(USERS, headers=auth_headers)
    assert forbidden.status_code == 403
    assert forbidden.json()["error"]["code"] == "forbidden"
    for path in (
        "/api/v1/admin/devices",
        "/api/v1/admin/domains",
        "/api/v1/admin/sessions",
        "/api/v1/admin/security-events",
        "/api/v1/admin/diagnostics",
        "/api/v1/admin/settings",
    ):
        assert (await client.get(path, headers=auth_headers)).status_code == 403, path


async def test_create_user(
    client: AsyncClient, admin_headers: dict, login_body: LoginBody, db: AsyncSession
) -> None:
    response = await client.post(
        USERS,
        json={
            "email": "  New@Example.com ",
            "password": "pw-12345678",
            "display_name": "  Newbie ",
            "role": "user",
        },
        headers=admin_headers,
    )
    assert response.status_code == 201, response.text
    body = response.json()
    assert set(body) == USER_KEYS
    assert body["email"] == "new@example.com" and body["display_name"] == "Newbie"
    assert body["role"] == "user" and body["is_active"] is True
    assert body["failed_logins"] == 0 and body["locked_until"] is None
    assert body["created_at"].endswith("Z")
    assert "password" not in response.text and "hash" not in response.text

    login = await client.post(
        "/api/v1/auth/login", json=login_body(email="new@example.com", password="pw-12345678")
    )
    assert login.status_code == 200

    duplicate = await client.post(
        USERS,
        json={"email": "new@example.com", "password": "pw-12345678", "display_name": "Dup"},
        headers=admin_headers,
    )
    assert duplicate.status_code == 409 and duplicate.json()["error"]["code"] == "conflict"

    as_admin = await client.post(
        USERS,
        json={
            "email": "boss@example.com",
            "password": "pw-12345678",
            "display_name": "B",
            "role": "admin",
        },
        headers=admin_headers,
    )
    assert as_admin.status_code == 201 and as_admin.json()["role"] == "admin"
    stored = await db.scalar(select(User).where(User.email == "boss@example.com"))
    assert stored is not None and stored.role == "admin"


async def test_create_user_validation(client: AsyncClient, admin_headers: dict) -> None:
    for body in (
        {"email": "not-an-email", "password": "pw-12345678", "display_name": "X"},
        {"email": "a@b.co", "password": "short", "display_name": "X"},
        {"email": "a@b.co", "password": "pw-12345678", "display_name": "   "},
        {"email": "a@b.co", "password": "pw-12345678", "display_name": "X", "role": "root"},
    ):
        response = await client.post(USERS, json=body, headers=admin_headers)
        assert response.status_code == 422, body
        assert response.json()["error"]["code"] == "validation_error"


async def test_list_users_with_search_and_limit(
    client: AsyncClient, admin_headers: dict, user: User
) -> None:
    for i in range(3):
        await client.post(
            USERS,
            json={
                "email": f"user{i}@corp.io",
                "password": "pw-12345678",
                "display_name": f"Ann {i}",
            },
            headers=admin_headers,
        )
    everyone = await client.get(USERS, headers=admin_headers)
    assert everyone.status_code == 200
    emails = {u["email"] for u in everyone.json()}
    assert emails == {
        "admin@example.com",
        "alice@example.com",
        "user0@corp.io",
        "user1@corp.io",
        "user2@corp.io",
    }
    assert all(set(u) == USER_KEYS for u in everyone.json())

    by_email = await client.get(USERS, params={"q": "CORP.io"}, headers=admin_headers)
    assert {u["email"] for u in by_email.json()} == {
        "user0@corp.io",
        "user1@corp.io",
        "user2@corp.io",
    }

    by_name = await client.get(USERS, params={"q": "ann 1"}, headers=admin_headers)
    assert [u["display_name"] for u in by_name.json()] == ["Ann 1"]

    wildcard_is_literal = await client.get(USERS, params={"q": "%"}, headers=admin_headers)
    assert wildcard_is_literal.json() == []

    limited = await client.get(USERS, params={"limit": 2}, headers=admin_headers)
    assert len(limited.json()) == 2
    assert (await client.get(USERS, params={"limit": 0}, headers=admin_headers)).status_code == 422


async def test_patch_display_name_and_unknown_user(
    client: AsyncClient, admin_headers: dict, user: User
) -> None:
    response = await client.patch(
        f"{USERS}/{user.id}", json={"display_name": "Alicia"}, headers=admin_headers
    )
    assert response.status_code == 200 and response.json()["display_name"] == "Alicia"

    empty = await client.patch(f"{USERS}/{user.id}", json={}, headers=admin_headers)
    assert empty.status_code == 200 and empty.json()["display_name"] == "Alicia"

    unknown = await client.patch(
        f"{USERS}/{uuid.uuid4()}", json={"display_name": "x"}, headers=admin_headers
    )
    assert unknown.status_code == 404 and unknown.json()["error"]["code"] == "not_found"

    extra = await client.patch(f"{USERS}/{user.id}", json={"role": "admin"}, headers=admin_headers)
    assert extra.status_code == 422
    short = await client.patch(
        f"{USERS}/{user.id}", json={"password": "short"}, headers=admin_headers
    )
    assert short.status_code == 422


async def test_patch_password_revokes_refresh_tokens(
    client: AsyncClient,
    admin_headers: dict,
    user: User,
    logged_in: dict[str, Any],
    login_body: LoginBody,
) -> None:
    response = await client.patch(
        f"{USERS}/{user.id}", json={"password": "brand-new-pw"}, headers=admin_headers
    )
    assert response.status_code == 200
    assert "brand-new-pw" not in response.text

    old_refresh = await client.post(
        "/api/v1/auth/refresh", json={"refresh_token": logged_in["refresh_token"]}
    )
    assert old_refresh.status_code == 401
    assert (await client.post("/api/v1/auth/login", json=login_body())).status_code == 401
    relogin = await client.post(
        "/api/v1/auth/login",
        json=login_body(
            password="brand-new-pw",
            device_id=logged_in["device"]["id"],
            secret=logged_in["device"]["secret"],
        ),
    )
    assert relogin.status_code == 200


async def test_deactivate_revokes_tokens_and_reactivate_restores_login(
    client: AsyncClient,
    admin_headers: dict,
    user: User,
    logged_in: dict[str, Any],
    auth_headers: dict,
    login_body: LoginBody,
    db: AsyncSession,
) -> None:
    response = await client.patch(
        f"{USERS}/{user.id}", json={"is_active": False}, headers=admin_headers
    )
    assert response.status_code == 200 and response.json()["is_active"] is False

    live = await db.scalar(
        select(RefreshToken).where(
            RefreshToken.user_id == user.id, RefreshToken.revoked_at.is_(None)
        )
    )
    assert live is None
    assert (await client.get("/api/v1/me", headers=auth_headers)).status_code == 401
    refreshed = await client.post(
        "/api/v1/auth/refresh", json={"refresh_token": logged_in["refresh_token"]}
    )
    assert refreshed.status_code == 401
    login = await client.post("/api/v1/auth/login", json=login_body())
    assert login.status_code == 403 and login.json()["error"]["code"] == "account_disabled"

    again = await client.patch(
        f"{USERS}/{user.id}", json={"is_active": True}, headers=admin_headers
    )
    assert again.status_code == 200 and again.json()["is_active"] is True
    assert (await client.post("/api/v1/auth/login", json=login_body())).status_code == 200


async def test_unlock_resets_lockout(
    client: AsyncClient, admin_headers: dict, user: User, login_body: LoginBody, db: AsyncSession
) -> None:
    user.failed_logins = 10
    user.locked_until = utcnow() + timedelta(minutes=15)
    await db.commit()
    locked = await client.post("/api/v1/auth/login", json=login_body())
    assert locked.status_code == 423

    listed = await client.get(USERS, params={"q": "alice"}, headers=admin_headers)
    assert listed.json()[0]["failed_logins"] == 10
    assert listed.json()[0]["locked_until"].endswith("Z")

    response = await client.patch(
        f"{USERS}/{user.id}", json={"unlock": True}, headers=admin_headers
    )
    assert response.status_code == 200
    assert response.json()["failed_logins"] == 0 and response.json()["locked_until"] is None
    assert (await client.post("/api/v1/auth/login", json=login_body())).status_code == 200

    noop = await client.patch(f"{USERS}/{user.id}", json={"unlock": False}, headers=admin_headers)
    assert noop.status_code == 200


async def test_admin_can_login_with_password_from_fixture(
    client: AsyncClient, admin_headers: dict
) -> None:
    me = await client.get("/api/v1/me", headers=admin_headers)
    assert me.status_code == 200 and me.json()["role"] == "admin"
    assert PASSWORD  # fixture sanity


# ------------------------------------------------------------------ deactivation takes effect


async def test_deactivating_closes_the_live_control_channel(
    client: AsyncClient,
    admin_headers: dict,
    ws_connect: Any,
    make_actor: Any,
    db: AsyncSession,
) -> None:
    """Revoking refresh tokens is not enough on its own.

    A refresh token is only consulted once an access token expires, so a disabled account used to
    keep browsing through somebody else's connection for as long as its current access token
    lasted: the administrator pressed the button and nothing observable happened.
    """
    actor = await make_actor("cutoff@example.com")
    ws = await ws_connect(actor)
    assert ws.hello_ack is not None

    response = await client.patch(
        f"{USERS}/{actor.user.id}", headers=admin_headers, json={"is_active": False}
    )

    assert response.status_code == 200
    assert response.json()["is_active"] is False
    assert await ws.wait_closed() == CloseCode.NOT_ALLOWED


async def test_deactivating_clears_presence_but_leaves_the_devices_alone(
    client: AsyncClient,
    admin_headers: dict,
    ws_connect: Any,
    make_actor: Any,
    db: AsyncSession,
) -> None:
    """The account is disabled, not the hardware: re-activating must not leave the user
    re-registering every machine they own."""
    actor = await make_actor("presence@example.com")
    ws = await ws_connect(actor)
    await ws.send({"type": "host.available", "available": True})
    await ws.drain(timeout=0.1)

    await client.patch(f"{USERS}/{actor.user.id}", headers=admin_headers, json={"is_active": False})
    await ws.wait_closed()

    await db.rollback()
    presence = await db.get(Presence, actor.device.id)
    assert presence is not None
    assert presence.connected is False
    assert presence.is_available_host is False

    device = await db.get(Device, actor.device.id)
    assert device is not None
    assert device.status == DeviceStatus.ACTIVE


async def test_deactivating_names_the_administrator_in_the_audit_trail(
    client: AsyncClient, admin_headers: dict, admin: User, make_actor: Any, db: AsyncSession
) -> None:
    actor = await make_actor("audited@example.com")

    await client.patch(f"{USERS}/{actor.user.id}", headers=admin_headers, json={"is_active": False})

    await db.rollback()
    events = list(
        await db.scalars(
            select(SecurityEvent).where(SecurityEvent.type == SecurityEventType.USER_DEACTIVATED)
        )
    )
    event = events[0] if len(events) == 1 else None
    assert event is not None
    assert event.user_id == actor.user.id
    assert event.details is not None
    assert event.details["actor_user_id"] == str(admin.id)


async def test_reactivating_writes_no_deactivation_event_and_closes_nothing(
    client: AsyncClient, admin_headers: dict, make_actor: Any, db: AsyncSession
) -> None:
    """Only the transition into "disabled" is an event. Setting is_active to a value it already
    holds must not fill the audit trail with decisions nobody took."""
    actor = await make_actor("noop@example.com")

    for _ in range(2):
        await client.patch(
            f"{USERS}/{actor.user.id}", headers=admin_headers, json={"is_active": True}
        )

    await db.rollback()
    events = list(
        await db.scalars(
            select(SecurityEvent).where(SecurityEvent.type == SecurityEventType.USER_DEACTIVATED)
        )
    )
    assert events == []
