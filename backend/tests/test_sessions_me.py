import uuid
from datetime import timedelta
from typing import Any

from httpx import AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.models import Device, User
from app.models.enums import SessionStatus
from app.services.users import create_user
from tests.conftest import DeviceFactory, SessionFactory

SESSIONS_ME = "/api/v1/sessions/me"
KEYS = {
    "id",
    "role",
    "peer_display_name",
    "peer_device_name",
    "status",
    "created_at",
    "started_at",
    "ended_at",
    "end_reason",
    "bytes_up",
    "bytes_down",
}


async def test_sessions_me_empty_and_auth(client: AsyncClient, auth_headers: dict) -> None:
    assert (await client.get(SESSIONS_ME)).status_code == 401
    response = await client.get(SESSIONS_ME, headers=auth_headers)
    assert response.status_code == 200 and response.json() == []


async def test_sessions_me_roles_peers_and_order(
    client: AsyncClient,
    auth_headers: dict,
    user: User,
    logged_in: dict[str, Any],
    db: AsyncSession,
    make_device: DeviceFactory,
    make_session: SessionFactory,
) -> None:
    my_device = await db.get(Device, uuid.UUID(logged_in["device"]["id"]))
    assert my_device is not None
    other = await create_user(
        db, email="bob@example.com", password="pw-12345678", display_name="Bob"
    )
    other_device = await make_device(other, "BOB-PC")
    third = await create_user(
        db, email="carol@example.com", password="pw-12345678", display_name="Carol"
    )
    third_device = await make_device(third, "CAROL-PC")
    await db.commit()

    now = utcnow()
    as_guest = await make_session(
        user,
        my_device,
        other,
        other_device,
        status=SessionStatus.ENDED,
        created_at=now - timedelta(hours=3),
        started_at=now - timedelta(hours=3),
        ended_at=now - timedelta(hours=2),
        end_reason="host_ended",
        bytes_up=100,
        bytes_down=2000,
    )
    as_host = await make_session(
        other,
        other_device,
        user,
        my_device,
        status=SessionStatus.ACTIVE,
        created_at=now - timedelta(hours=1),
        started_at=now,
    )
    await make_session(
        third, third_device, other, other_device, status=SessionStatus.CONNECTING, created_at=now
    )

    response = await client.get(SESSIONS_ME, headers=auth_headers)
    assert response.status_code == 200
    rows = response.json()
    assert [r["id"] for r in rows] == [str(as_host.id), str(as_guest.id)]
    assert all(set(r) == KEYS for r in rows)

    host_row, guest_row = rows
    assert host_row["role"] == "host" and host_row["status"] == "active"
    assert host_row["peer_display_name"] == "Bob" and host_row["peer_device_name"] == "BOB-PC"
    assert host_row["ended_at"] is None and host_row["end_reason"] is None
    assert host_row["started_at"].endswith("Z") and host_row["created_at"].endswith("Z")

    assert guest_row["role"] == "guest" and guest_row["status"] == "ended"
    assert guest_row["peer_display_name"] == "Bob" and guest_row["peer_device_name"] == "BOB-PC"
    assert guest_row["end_reason"] == "host_ended"
    assert guest_row["bytes_up"] == 100 and guest_row["bytes_down"] == 2000

    limited = await client.get(SESSIONS_ME, params={"limit": 1}, headers=auth_headers)
    assert [r["id"] for r in limited.json()] == [str(as_host.id)]
    assert (
        await client.get(SESSIONS_ME, params={"limit": 0}, headers=auth_headers)
    ).status_code == 422
