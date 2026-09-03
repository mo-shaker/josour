import uuid
from datetime import timedelta
from typing import Any

from httpx import AsyncClient
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.models import Device, SecurityEvent, User
from app.models.enums import DeviceStatus
from app.services.users import create_user
from tests.conftest import PASSWORD, LoginBody

LOGIN = "/api/v1/auth/login"


async def test_first_login_registers_device_and_returns_secret_once(
    client: AsyncClient, user: User, login_body: LoginBody, db: AsyncSession
) -> None:
    first = await client.post(LOGIN, json=login_body())
    assert first.status_code == 200, first.text
    body = first.json()
    assert set(body) == {"access_token", "refresh_token", "expires_in", "user", "device"}
    assert body["expires_in"] == 900
    assert body["user"] == {
        "id": str(user.id),
        "email": "alice@example.com",
        "display_name": "Alice",
        "role": "user",
    }
    assert set(body["device"]) == {"id", "name", "secret"}
    assert body["device"]["name"] == "LAPTOP-01"
    assert isinstance(body["device"]["secret"], str) and len(body["device"]["secret"]) >= 32

    device = await db.get(Device, uuid.UUID(body["device"]["id"]))
    assert device is not None and device.user_id == user.id
    assert device.status == DeviceStatus.ACTIVE
    assert body["device"]["secret"] not in device.device_secret_hash

    second = await client.post(
        LOGIN, json=login_body(device_id=body["device"]["id"], secret=body["device"]["secret"])
    )
    assert second.status_code == 200, second.text
    assert second.json()["device"] == {
        "id": body["device"]["id"],
        "name": "LAPTOP-01",
        "secret": None,
    }
    device_count = select(func.count()).select_from(Device).where(Device.user_id == user.id)
    assert await db.scalar(device_count) == 1


async def test_login_with_wrong_device_secret_is_unauthorized(
    client: AsyncClient, logged_in: dict[str, Any], login_body: LoginBody
) -> None:
    response = await client.post(
        LOGIN, json=login_body(device_id=logged_in["device"]["id"], secret="not-the-secret")
    )
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "unauthorized"


async def test_login_with_unknown_device_id_is_unauthorized(
    client: AsyncClient, user: User, login_body: LoginBody
) -> None:
    response = await client.post(
        LOGIN, json=login_body(device_id="0d3c5d4a-9f1e-4d0b-8a1e-2f8a0c7b6e5d", secret="x")
    )
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "unauthorized"


async def test_wrong_password_then_lockout_after_ten_failures(
    client: AsyncClient, user: User, login_body: LoginBody, db: AsyncSession
) -> None:
    for _ in range(9):
        response = await client.post(LOGIN, json=login_body(password="wrong"))
        assert response.status_code == 401
        assert response.json()["error"]["code"] == "invalid_credentials"

    tenth = await client.post(LOGIN, json=login_body(password="wrong"))
    assert tenth.status_code == 423
    assert tenth.json()["error"]["code"] == "account_locked"

    correct_while_locked = await client.post(LOGIN, json=login_body())
    assert correct_while_locked.status_code == 423

    await db.refresh(user)
    assert user.failed_logins == 10
    assert user.locked_until is not None

    events = (await db.scalars(select(SecurityEvent.type).order_by(SecurityEvent.created_at))).all()
    assert events.count("login_failed") == 9
    assert events.count("login_locked") == 2


async def test_lock_expiry_resets_counter_and_allows_login(
    client: AsyncClient, user: User, login_body: LoginBody, db: AsyncSession
) -> None:
    user.failed_logins = 10
    user.locked_until = utcnow() - timedelta(seconds=1)
    await db.commit()

    response = await client.post(LOGIN, json=login_body())
    assert response.status_code == 200, response.text
    await db.refresh(user)
    assert user.failed_logins == 0 and user.locked_until is None


async def test_successful_login_resets_failed_counter(
    client: AsyncClient, user: User, login_body: LoginBody, db: AsyncSession
) -> None:
    for _ in range(3):
        await client.post(LOGIN, json=login_body(password="wrong"))
    await db.refresh(user)
    assert user.failed_logins == 3

    assert (await client.post(LOGIN, json=login_body())).status_code == 200
    await db.refresh(user)
    assert user.failed_logins == 0


async def test_inactive_user_is_account_disabled(
    client: AsyncClient, user: User, login_body: LoginBody, db: AsyncSession
) -> None:
    user.is_active = False
    await db.commit()
    response = await client.post(LOGIN, json=login_body())
    assert response.status_code == 403
    assert response.json()["error"]["code"] == "account_disabled"


async def test_unknown_email_is_invalid_credentials(
    client: AsyncClient, login_body: LoginBody, db: AsyncSession
) -> None:
    response = await client.post(LOGIN, json=login_body(email="nobody@example.com"))
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "invalid_credentials"
    event = await db.scalar(select(SecurityEvent))
    assert event is not None and event.type == "login_failed" and event.ip == "127.0.0.1"


async def test_revoked_device_is_forbidden(
    client: AsyncClient, logged_in: dict[str, Any], login_body: LoginBody, db: AsyncSession
) -> None:
    device = await db.get(Device, uuid.UUID(logged_in["device"]["id"]))
    assert device is not None
    device.status = DeviceStatus.REVOKED
    await db.commit()

    response = await client.post(
        LOGIN,
        json=login_body(device_id=logged_in["device"]["id"], secret=logged_in["device"]["secret"]),
    )
    assert response.status_code == 403
    assert response.json()["error"]["code"] == "device_revoked"


async def test_validation_error_envelope(client: AsyncClient) -> None:
    response = await client.post(LOGIN, json={"email": "alice@example.com", "password": "x"})
    assert response.status_code == 422
    error = response.json()["error"]
    assert error["code"] == "validation_error"
    assert "device" in error["message"]


async def test_login_records_success_event_with_ip(
    client: AsyncClient, logged_in: dict[str, Any], db: AsyncSession
) -> None:
    event = await db.scalar(select(SecurityEvent).where(SecurityEvent.type == "login_success"))
    assert event is not None
    assert event.ip == "127.0.0.1"
    assert str(event.device_id) == logged_in["device"]["id"]
    assert event.details == {"registered_device": True}


async def test_email_is_case_insensitive(
    client: AsyncClient, db: AsyncSession, login_body: LoginBody
) -> None:
    await create_user(db, email="Bob@Example.com", password=PASSWORD, display_name="Bob")
    await db.commit()
    response = await client.post(LOGIN, json=login_body(email="  BOB@example.COM "))
    assert response.status_code == 200, response.text
    assert response.json()["user"]["email"] == "bob@example.com"
