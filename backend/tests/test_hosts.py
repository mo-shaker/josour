import uuid
from datetime import timedelta
from typing import Any

from httpx import AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.models import ConnectionRequest, Device, Presence, Session, User
from app.models.enums import RequestStatus, SessionStatus
from app.services.users import create_user

HOSTS = "/api/v1/hosts"


async def test_hosts_empty(client: AsyncClient, auth_headers: dict) -> None:
    response = await client.get(HOSTS, headers=auth_headers)
    assert response.status_code == 200
    assert response.json() == []


async def test_hosts_requires_auth(client: AsyncClient) -> None:
    assert (await client.get(HOSTS)).status_code == 401


async def _make_device(db: AsyncSession, user: User, name: str) -> Device:
    device = Device(user_id=user.id, name=name, os_version="Windows 11", device_secret_hash="x")
    db.add(device)
    await db.flush()
    return device


async def test_hosts_lists_available_and_excludes_busy(
    client: AsyncClient, auth_headers: dict, logged_in: dict[str, Any], db: AsyncSession
) -> None:
    host_user = await create_user(
        db, email="host@example.com", password="pw-12345678", display_name="Hosty"
    )
    host_device = await _make_device(db, host_user, "OFFICE-PC")
    db.add(
        Presence(device_id=host_device.id, connected=True, is_available_host=True, reachable=True)
    )
    idle_user = await create_user(
        db, email="idle@example.com", password="pw-12345678", display_name="Idle"
    )
    idle_device = await _make_device(db, idle_user, "IDLE-PC")
    db.add(Presence(device_id=idle_device.id, connected=True, is_available_host=False))
    await db.commit()

    response = await client.get(HOSTS, headers=auth_headers)
    assert response.json() == [
        {
            "device_id": str(host_device.id),
            "user_display_name": "Hosty",
            "device_name": "OFFICE-PC",
            "reachable": True,
        }
    ]

    guest_device_id = uuid.UUID(logged_in["device"]["id"])
    guest_user_id = uuid.UUID(logged_in["user"]["id"])
    request = ConnectionRequest(
        guest_user_id=guest_user_id,
        guest_device_id=guest_device_id,
        host_user_id=host_user.id,
        host_device_id=host_device.id,
        requested_minutes=30,
        status=RequestStatus.ACCEPTED,
        expires_at=utcnow() + timedelta(seconds=60),
    )
    db.add(request)
    await db.flush()
    session = Session(
        request_id=request.id,
        guest_user_id=guest_user_id,
        guest_device_id=guest_device_id,
        host_user_id=host_user.id,
        host_device_id=host_device.id,
        status=SessionStatus.CONNECTING,
        expires_at=utcnow() + timedelta(minutes=30),
    )
    db.add(session)
    await db.commit()

    assert (await client.get(HOSTS, headers=auth_headers)).json() == []

    session.status = SessionStatus.ENDED
    session.end_reason = "guest_ended"
    await db.commit()
    assert len((await client.get(HOSTS, headers=auth_headers)).json()) == 1
