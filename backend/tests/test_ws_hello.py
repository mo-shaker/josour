"""``/ws`` handshake, heartbeat and envelope handling (docs/ws-protocol.md sections 1-3, 7)."""

import uuid
from typing import Any

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import ConnectDiagnostic, Presence
from app.models.enums import DeviceStatus
from app.ws import router as ws_router
from app.ws.protocol import CloseCode
from tests.conftest import ActorFactory, WsFactory
from tests.ws_client import ASGIWebSocket


async def test_hello_ack_contents(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    actor = await make_actor("alice@example.com", display_name="Alice")
    ws = await ws_connect(actor, diagnostics={"firewall_rule_present": True, "os_build": "22631"})

    ack = ws.hello_ack
    assert ack is not None
    assert ack["type"] == "hello.ack"
    assert ack["server_time"].endswith("Z")
    assert ack["public_ip"] == "203.0.113.10"
    assert ack["allowlist_version"] == 0
    assert ack["settings"] == {
        "max_session_minutes": 120,
        "request_timeout_seconds": 60,
        "allowed_ports": [80, 443],
        "log_domains": False,
    }
    assert ws.snapshot == {"type": "hosts.snapshot", "hosts": []}

    presence = await db.get(Presence, actor.device.id)
    assert presence is not None
    assert (presence.connected, presence.is_available_host) == (True, False)
    assert presence.public_ip == "203.0.113.10"
    stored = await db.scalar(
        select(ConnectDiagnostic.data).where(ConnectDiagnostic.device_id == actor.device.id)
    )
    assert stored == {"firewall_rule_present": True, "os_build": "22631"}


async def test_public_ip_uses_forwarded_header_behind_the_proxy(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    actor = await make_actor("proxied@example.com")
    ws = await ws_connect(
        actor,
        client=("10.0.0.2", 40000),  # the reverse proxy on the private network
        headers={"x-forwarded-for": "198.51.100.7, 10.0.0.2"},
    )
    assert ws.hello_ack is not None
    assert ws.hello_ack["public_ip"] == "198.51.100.7"


async def test_forwarded_header_is_ignored_from_a_public_peer(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    actor = await make_actor("direct@example.com")
    ws = await ws_connect(
        actor, client=("93.184.216.34", 40000), headers={"x-forwarded-for": "10.9.9.9"}
    )
    assert ws.hello_ack is not None
    assert ws.hello_ack["public_ip"] == "93.184.216.34"


async def test_no_hello_within_the_deadline_closes_4401(
    app: Any, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(ws_router, "HELLO_TIMEOUT_SECONDS", 0.05)
    async with ASGIWebSocket(app) as ws:
        assert ws.accepted
        assert await ws.wait_closed() == CloseCode.NO_HELLO


async def test_invalid_token_closes_4401(ws_connect: WsFactory) -> None:
    ws = await ws_connect(token="not-a-jwt", device_id=str(uuid.uuid4()))
    assert await ws.wait_closed() == CloseCode.NO_HELLO


async def test_device_id_must_match_the_token(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    actor = await make_actor("mismatch@example.com")
    ws = await ws_connect(token=actor.token, device_id=str(uuid.uuid4()))
    assert await ws.wait_closed() == CloseCode.NO_HELLO


async def test_first_frame_must_be_hello(app: Any) -> None:
    async with ASGIWebSocket(app) as ws:
        await ws.send({"type": "ping"})
        assert (await ws.receive())["code"] == "unauthorized"
        assert await ws.wait_closed() == CloseCode.NO_HELLO


async def test_malformed_first_frame_closes_4401(app: Any) -> None:
    async with ASGIWebSocket(app) as ws:
        await ws.send({"type": "hello"})  # no token, no device_id
        error = await ws.receive()
        assert error["code"] == "bad_request"
        assert "token" in error["message"]
        assert await ws.wait_closed() == CloseCode.NO_HELLO


async def test_revoked_device_closes_4403(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    actor = await make_actor("revoked@example.com")
    actor.device.status = DeviceStatus.REVOKED
    await db.commit()
    ws = await ws_connect(actor)
    assert await ws.wait_closed() == CloseCode.NOT_ALLOWED


async def test_disabled_user_closes_4403(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    actor = await make_actor("disabled@example.com")
    actor.user.is_active = False
    await db.commit()
    ws = await ws_connect(actor)
    assert await ws.wait_closed() == CloseCode.NOT_ALLOWED


async def test_second_connection_for_the_same_device_closes_the_first_with_4409(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    actor = await make_actor("twice@example.com")
    first = await ws_connect(actor)
    second = await ws_connect(actor)

    assert await first.wait_closed() == CloseCode.SUPERSEDED
    assert second.hello_ack is not None

    # The superseded connection must not have torn down the newcomer's presence row.
    await db.rollback()
    presence = await db.get(Presence, actor.device.id)
    assert presence is not None and presence.connected is True

    await second.send({"type": "ping"})
    assert (await second.expect("pong"))["type"] == "pong"


async def test_client_ping_is_answered_with_pong(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    actor = await make_actor("pinger@example.com")
    ws = await ws_connect(actor)
    await ws.send({"type": "ping"})
    assert await ws.expect("pong") == {"type": "pong"}


async def test_server_pings_and_a_pong_keeps_the_connection_alive(
    ws_connect: WsFactory, make_actor: ActorFactory, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(ws_router, "PING_INTERVAL_SECONDS", 0.04)
    monkeypatch.setattr(ws_router, "DEAD_AFTER_SECONDS", 30.0)
    actor = await make_actor("beat@example.com")
    ws = await ws_connect(actor)

    assert await ws.expect("ping", skip=frozenset({"hosts.update"})) == {"type": "ping"}
    await ws.send({"type": "pong"})
    assert await ws.expect("ping", skip=frozenset({"hosts.update"})) == {"type": "ping"}
    assert ws.close_code is None


async def test_a_connection_that_stops_answering_is_closed(
    ws_connect: WsFactory, make_actor: ActorFactory, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(ws_router, "PING_INTERVAL_SECONDS", 0.04)
    monkeypatch.setattr(ws_router, "DEAD_AFTER_SECONDS", 0.12)
    actor = await make_actor("silent@example.com")
    ws = await ws_connect(actor)

    assert await ws.wait_closed(timeout=2.0) == CloseCode.GOING_AWAY


async def test_unknown_and_invalid_frames_answer_bad_request_with_the_ref(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    actor = await make_actor("sloppy@example.com")
    ws = await ws_connect(actor)

    await ws.send({"type": "session.stats", "ref": "r1", "session_id": str(uuid.uuid4())})
    unknown = await ws.expect("error")
    assert (unknown["code"], unknown["ref"]) == ("bad_request", "r1")

    await ws.send({"type": "request.create", "ref": "r2", "duration_min": "long"})
    invalid = await ws.expect("error")
    assert (invalid["code"], invalid["ref"]) == ("bad_request", "r2")

    await ws.send({"type": "hello", "token": "x", "device_id": actor.device_id})
    assert (await ws.expect("error"))["code"] == "bad_request"

    # ... and the connection is still usable afterwards.
    await ws.send({"type": "ping"})
    assert await ws.expect("pong") == {"type": "pong"}


async def test_non_json_frame_answers_bad_request(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    actor = await make_actor("garbage@example.com")
    ws = await ws_connect(actor)
    await ws.send_text("not json")
    error = await ws.expect("error")
    assert error["code"] == "bad_request" and error["ref"] is None


async def test_disconnect_clears_presence(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    actor = await make_actor("bye@example.com")
    ws = await ws_connect(actor)
    await ws.send({"type": "host.available", "available": True})
    await ws.receive()  # hosts.update
    await ws.disconnect()

    await db.rollback()
    presence = await db.get(Presence, actor.device.id)
    assert presence is not None
    assert (presence.connected, presence.is_available_host) == (False, False)


async def test_revoking_a_device_drops_its_control_channel(
    ws_connect: WsFactory, make_actor: ActorFactory, client: Any, db: AsyncSession
) -> None:
    from app.models import Presence
    from app.models.enums import UserRole

    admin = await make_actor("admin@example.com", display_name="Admin", role=UserRole.ADMIN)
    victim = await make_actor("victim@example.com")
    device_id = victim.device.id
    ws = await ws_connect(victim)
    await ws.send({"type": "host.available", "available": True})
    await ws.drain(timeout=0.1)

    response = await client.post(
        f"/api/v1/admin/devices/{device_id}/revoke",
        headers={"Authorization": f"Bearer {admin.token}"},
    )
    assert response.status_code == 204, response.text

    assert await ws.wait_closed() == CloseCode.NOT_ALLOWED
    db.expire_all()
    presence = await db.get(Presence, device_id)
    assert presence is not None and presence.connected is False
