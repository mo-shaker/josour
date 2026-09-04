"""Process lifespan and the event-bus hooks of the WebSocket layer."""

import uuid
from typing import Any

from httpx import AsyncClient
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Presence, Session
from app.models.enums import SessionStatus, UserRole
from app.services.session_timer import scheduler
from app.ws.connection_manager import connection_manager
from app.ws.protocol import CloseCode
from tests.conftest import ActorFactory, SessionFactory, WsFactory


def _headers(token: str) -> dict[str, str]:
    return {"Authorization": f"Bearer {token}"}


async def test_startup_resets_presence_and_ends_dangling_sessions(
    make_actor: ActorFactory,
    make_session: SessionFactory,
    db: AsyncSession,
    run_lifespan: Any,
) -> None:
    guest = await make_actor("guest@example.com")
    host = await make_actor("host@example.com", device_name="OFFICE-PC")
    session_id = (
        await make_session(
            guest.user, guest.device, host.user, host.device, status=SessionStatus.CONNECTING
        )
    ).id
    guest_device_id, host_device_id = guest.device.id, host.device.id
    db.add_all(
        [
            Presence(device_id=guest.device.id, connected=True, is_available_host=False),
            Presence(
                device_id=host.device.id,
                connected=True,
                is_available_host=True,
                reachable=True,
            ),
        ]
    )
    await db.commit()

    async with run_lifespan():
        db.expire_all()
        reloaded = await db.get(Session, session_id)
        assert reloaded is not None
        assert reloaded.status == SessionStatus.ENDED
        assert reloaded.end_reason == "host_disconnected"
        for device_id in (guest_device_id, host_device_id):
            row = await db.get(Presence, device_id)
            assert row is not None
            assert (row.connected, row.is_available_host, row.reachable) == (False, False, None)


async def test_shutdown_closes_every_connection_with_1012(
    ws_connect: WsFactory, make_actor: ActorFactory, run_lifespan: Any
) -> None:
    async with run_lifespan():
        actor = await make_actor("bye@example.com")
        ws = await ws_connect(actor)
        assert ws.hello_ack is not None
    assert await ws.wait_closed() == CloseCode.SERVER_RESTART
    assert len(connection_manager) == 0
    assert len(scheduler) == 0


async def test_publishing_the_allowlist_broadcasts_allowlist_updated(
    ws_connect: WsFactory, make_actor: ActorFactory, client: AsyncClient
) -> None:
    admin = await make_actor("admin2@example.com", display_name="Admin", role=UserRole.ADMIN)
    listener = await make_actor("listener@example.com")
    ws = await ws_connect(listener)
    await ws.drain(timeout=0.1)

    response = await client.put(
        "/api/v1/admin/domains",
        headers=_headers(admin.token),
        json={"entries": ["example.com", "=exact.com"]},
    )
    assert response.status_code == 200, response.text

    assert await ws.expect("allowlist.updated") == {"type": "allowlist.updated", "version": 1}


async def test_admin_termination_sends_session_terminate_to_both_parties(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    make_session: SessionFactory,
    client: AsyncClient,
    db: AsyncSession,
) -> None:
    admin = await make_actor("admin3@example.com", display_name="Admin", role=UserRole.ADMIN)
    guest = await make_actor("guest3@example.com")
    host = await make_actor("host3@example.com", device_name="OFFICE-PC")
    session_id = (await make_session(guest.user, guest.device, host.user, host.device)).id

    guest_ws = await ws_connect(guest)
    host_ws = await ws_connect(host)
    await guest_ws.drain(timeout=0.1)
    await host_ws.drain(timeout=0.1)

    response = await client.post(
        f"/api/v1/admin/sessions/{session_id}/terminate", headers=_headers(admin.token)
    )
    assert response.status_code == 204, response.text

    expected = {
        "type": "session.terminate",
        "session_id": str(session_id),
        "reason": "admin_terminated",
    }
    assert await guest_ws.expect("session.terminate") == expected
    assert await host_ws.expect("session.terminate") == expected

    db.expire_all()
    reloaded = await db.get(Session, session_id)
    assert reloaded is not None and reloaded.status == SessionStatus.ENDED


async def test_session_terminate_for_an_unknown_device_is_harmless(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    """The subscriber must tolerate peers that are not connected (nobody to send to)."""
    from app.core.clock import utcnow
    from app.services.events import SessionEnded, event_bus

    actor = await make_actor("nobody@example.com")
    ws = await ws_connect(actor)
    await ws.drain(timeout=0.1)

    delivered = await event_bus.publish(
        SessionEnded(
            session_id=uuid.uuid4(),
            guest_user_id=uuid.uuid4(),
            guest_device_id=uuid.uuid4(),
            host_user_id=uuid.uuid4(),
            host_device_id=uuid.uuid4(),
            reason="expired",
            ended_at=utcnow(),
        )
    )
    assert delivered == 1
    assert [frame["type"] for frame in await ws.drain(timeout=0.1)] == ["hosts.update"]
