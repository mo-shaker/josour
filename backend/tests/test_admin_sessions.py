import uuid
from datetime import timedelta
from typing import Any

from httpx import AsyncClient
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.models import SecurityEvent, Session, SessionKey, User
from app.models.enums import SessionStatus
from app.services.events import SessionEnded, event_bus
from app.services.users import create_user
from tests.conftest import DeviceFactory, SessionFactory

SESSIONS = "/api/v1/admin/sessions"
SESSION_KEYS = {
    "id",
    "status",
    "guest_user_id",
    "guest_display_name",
    "guest_device_id",
    "guest_device_name",
    "host_user_id",
    "host_display_name",
    "host_device_id",
    "host_device_name",
    "created_at",
    "started_at",
    "expires_at",
    "ended_at",
    "end_reason",
    "bytes_up",
    "bytes_down",
    "connect_result",
    "winner_type",
    "tls_version",
    "connect_ms",
}


async def _peers(db: AsyncSession, make_device: DeviceFactory) -> tuple[User, Any, User, Any]:
    guest = await create_user(
        db, email="guest@example.com", password="pw-12345678", display_name="Guest"
    )
    host = await create_user(
        db, email="host@example.com", password="pw-12345678", display_name="Hosty"
    )
    guest_device = await make_device(guest, "GUEST-PC")
    host_device = await make_device(host, "OFFICE-PC")
    await db.commit()
    return guest, guest_device, host, host_device


async def test_list_sessions_newest_first_with_names_and_filters(
    client: AsyncClient,
    admin_headers: dict,
    db: AsyncSession,
    make_device: DeviceFactory,
    make_session: SessionFactory,
) -> None:
    guest, guest_device, host, host_device = await _peers(db, make_device)
    now = utcnow()
    ended = await make_session(
        guest,
        guest_device,
        host,
        host_device,
        status=SessionStatus.ENDED,
        created_at=now - timedelta(hours=2),
        ended_at=now - timedelta(hours=1),
        end_reason="guest_ended",
        bytes_up=10,
        bytes_down=20,
        connect_result="ok",
        winner_type="lan",
        tls_version="1.3",
        connect_ms=42,
    )
    active = await make_session(
        guest,
        guest_device,
        host,
        host_device,
        status=SessionStatus.ACTIVE,
        created_at=now,
        started_at=now,
    )

    response = await client.get(SESSIONS, headers=admin_headers)
    assert response.status_code == 200
    rows = response.json()
    assert [r["id"] for r in rows] == [str(active.id), str(ended.id)]
    assert all(set(r) == SESSION_KEYS for r in rows)
    assert rows[0]["guest_display_name"] == "Guest" and rows[0]["guest_device_name"] == "GUEST-PC"
    assert rows[0]["host_display_name"] == "Hosty" and rows[0]["host_device_name"] == "OFFICE-PC"
    assert rows[0]["status"] == "active" and rows[0]["started_at"].endswith("Z")
    assert rows[1]["end_reason"] == "guest_ended" and rows[1]["connect_ms"] == 42
    assert rows[1]["winner_type"] == "lan" and rows[1]["bytes_down"] == 20

    only_ended = await client.get(SESSIONS, params={"status": "ended"}, headers=admin_headers)
    assert [r["id"] for r in only_ended.json()] == [str(ended.id)]
    limited = await client.get(SESSIONS, params={"limit": 1}, headers=admin_headers)
    assert [r["id"] for r in limited.json()] == [str(active.id)]
    assert (
        await client.get(SESSIONS, params={"status": "weird"}, headers=admin_headers)
    ).status_code == 422


async def test_terminate_session(
    client: AsyncClient,
    admin_headers: dict,
    admin: User,
    db: AsyncSession,
    make_device: DeviceFactory,
    make_session: SessionFactory,
) -> None:
    guest, guest_device, host, host_device = await _peers(db, make_device)
    session = await make_session(
        guest, guest_device, host, host_device, status=SessionStatus.ACTIVE
    )
    db.add(SessionKey(session_id=session.id, secret=b"\x01" * 32))
    await db.commit()
    ids = {
        "session": session.id,
        "guest": guest.id,
        "guest_device": guest_device.id,
        "host": host.id,
        "host_device": host_device.id,
        "admin": admin.id,
    }

    received: list[SessionEnded] = []
    unsubscribe = event_bus.subscribe(SessionEnded, received.append)
    try:
        response = await client.post(
            f"{SESSIONS}/{ids['session']}/terminate", headers=admin_headers
        )
    finally:
        unsubscribe()
    assert response.status_code == 204 and response.content == b""

    db.expire_all()  # the API wrote through another session; re-read everything
    stored = await db.get(Session, ids["session"])
    assert stored is not None
    assert stored.status == "ended" and stored.end_reason == "admin_terminated"
    assert stored.ended_at is not None
    assert await db.get(SessionKey, ids["session"]) is None

    event = await db.scalar(
        select(SecurityEvent).where(SecurityEvent.type == "session_admin_terminated")
    )
    assert event is not None and event.user_id == ids["admin"] and event.ip == "127.0.0.1"
    assert event.details == {
        # ``via`` distinguishes this from the ``end-session`` CLI, which has no admin user.
        "via": "api",
        "session_id": str(ids["session"]),
        "guest_user_id": str(ids["guest"]),
        "host_user_id": str(ids["host"]),
    }

    assert len(received) == 1
    ended = received[0]
    assert ended.session_id == ids["session"] and ended.reason == "admin_terminated"
    assert ended.guest_device_id == ids["guest_device"]
    assert ended.host_device_id == ids["host_device"]
    assert ended.guest_user_id == ids["guest"] and ended.host_user_id == ids["host"]
    assert ended.ended_at.tzinfo is not None

    again = await client.post(f"{SESSIONS}/{ids['session']}/terminate", headers=admin_headers)
    assert again.status_code == 409 and again.json()["error"]["code"] == "conflict"

    listed = await client.get(SESSIONS, params={"status": "ended"}, headers=admin_headers)
    assert listed.json()[0]["end_reason"] == "admin_terminated"


async def test_terminate_connecting_session_and_errors(
    client: AsyncClient,
    admin_headers: dict,
    auth_headers: dict,
    db: AsyncSession,
    make_device: DeviceFactory,
    make_session: SessionFactory,
) -> None:
    guest, guest_device, host, host_device = await _peers(db, make_device)
    connecting = await make_session(
        guest, guest_device, host, host_device, status=SessionStatus.CONNECTING
    )
    assert (
        await client.post(f"{SESSIONS}/{connecting.id}/terminate", headers=admin_headers)
    ).status_code == 204

    unknown = await client.post(f"{SESSIONS}/{uuid.uuid4()}/terminate", headers=admin_headers)
    assert unknown.status_code == 404 and unknown.json()["error"]["code"] == "not_found"
    assert (
        await client.post(f"{SESSIONS}/{connecting.id}/terminate", headers=auth_headers)
    ).status_code == 403
    assert (
        await client.post(f"{SESSIONS}/not-a-uuid/terminate", headers=admin_headers)
    ).status_code == 422
