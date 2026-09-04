"""Request lifecycle over the control channel (docs/ws-protocol.md section 5)."""

import base64
import uuid
from dataclasses import dataclass
from typing import Any

import pytest
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import ConnectionRequest, Session, SessionKey
from app.models.enums import RequestStatus, SessionStatus
from app.schemas.settings import AppSettings
from app.services import requests as request_service
from tests.conftest import Actor, ActorFactory, WsFactory
from tests.ws_client import ASGIWebSocket


@dataclass(slots=True)
class Pair:
    guest: Actor
    guest_ws: ASGIWebSocket
    host: Actor
    host_ws: ASGIWebSocket


async def _pair(ws_connect: WsFactory, make_actor: ActorFactory, *, available: bool = True) -> Pair:
    guest = await make_actor("guest@example.com", display_name="Guest", device_name="GUEST-PC")
    host = await make_actor("host@example.com", display_name="Hosty", device_name="OFFICE-PC")
    guest_ws = await ws_connect(guest)
    host_ws = await ws_connect(host, client=("198.51.100.9", 51000))
    if available:
        await host_ws.send({"type": "host.available", "available": True})
        await guest_ws.expect("hosts.update", skip=frozenset())
    await guest_ws.drain(timeout=0.1)
    await host_ws.drain(timeout=0.1)
    return Pair(guest=guest, guest_ws=guest_ws, host=host, host_ws=host_ws)


async def _open_request(pair: Pair, *, duration_min: int = 30) -> tuple[str, dict[str, Any]]:
    await pair.guest_ws.send(
        {
            "type": "request.create",
            "ref": "c1",
            "host_device_id": pair.host.device_id,
            "duration_min": duration_min,
        }
    )
    created = await pair.guest_ws.expect("request.created")
    incoming = await pair.host_ws.expect("request.incoming")
    assert created["ref"] == "c1"
    assert created["request_id"] == incoming["request_id"]
    return created["request_id"], incoming


# ---------------------------------------------------------------- happy paths


async def test_create_delivers_created_and_incoming(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, incoming = await _open_request(pair)

    assert incoming["guest_name"] == "Guest"
    assert incoming["guest_device"] == "GUEST-PC"
    assert incoming["duration_min"] == 30
    assert incoming["allowlist_version"] == 0
    assert incoming["expires_at"].endswith("Z")

    row = await db.get(ConnectionRequest, uuid.UUID(request_id))
    assert row is not None and row.status == RequestStatus.PENDING


async def test_accept_creates_the_session_and_tells_both_parties(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair, duration_min=45)

    await pair.host_ws.send({"type": "request.accept", "ref": "h1", "request_id": request_id})

    result = await pair.guest_ws.expect("request.result")
    assert result["accepted"] is True
    assert result["reason"] is None
    assert result["request_id"] == request_id
    session_id = result["session_id"]

    guest_created = await pair.guest_ws.expect("session.created")
    host_created = await pair.host_ws.expect("session.created")
    assert guest_created["session_id"] == host_created["session_id"] == session_id
    assert (guest_created["role"], host_created["role"]) == ("guest", "host")
    assert guest_created["secret_b64"] == host_created["secret_b64"]
    assert len(base64.b64decode(guest_created["secret_b64"])) == 32
    assert guest_created["peer"] == {"user_display_name": "Hosty", "device_name": "OFFICE-PC"}
    assert host_created["peer"] == {"user_display_name": "Guest", "device_name": "GUEST-PC"}
    assert guest_created["peer_public_ip"] == "198.51.100.9"
    assert host_created["peer_public_ip"] == "203.0.113.10"
    assert guest_created["same_public_ip"] is False
    assert guest_created["expires_at"].endswith("Z")
    assert guest_created["allowlist_version"] == 0

    session = await db.get(Session, uuid.UUID(session_id))
    assert session is not None and session.status == SessionStatus.CONNECTING
    key = await db.get(SessionKey, session.id)
    assert key is not None and len(key.secret) == 32
    request = await db.get(ConnectionRequest, uuid.UUID(request_id))
    assert request is not None and request.status == RequestStatus.ACCEPTED


async def test_a_busy_host_leaves_the_host_list(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)
    await pair.host_ws.send({"type": "request.accept", "ref": "h1", "request_id": request_id})
    await pair.guest_ws.expect("request.result")
    await pair.guest_ws.expect("session.created")

    assert (await pair.guest_ws.expect("hosts.update", skip=frozenset()))["hosts"] == []


async def test_reject(ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)

    await pair.host_ws.send({"type": "request.reject", "ref": "h1", "request_id": request_id})

    result = await pair.guest_ws.expect("request.result")
    assert (result["accepted"], result["reason"], result["session_id"]) == (
        False,
        "rejected",
        None,
    )
    row = await db.get(ConnectionRequest, uuid.UUID(request_id))
    assert row is not None and row.status == RequestStatus.REJECTED


async def test_cancel(ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)

    await pair.guest_ws.send({"type": "request.cancel", "ref": "c2", "request_id": request_id})

    result = await pair.guest_ws.expect("request.result")
    assert (result["accepted"], result["reason"]) == (False, "cancelled")
    expired = await pair.host_ws.expect("request.expired")
    assert expired["request_id"] == request_id
    row = await db.get(ConnectionRequest, uuid.UUID(request_id))
    assert row is not None and row.status == RequestStatus.CANCELLED


async def test_expiry_settles_the_request_on_its_own(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    monkeypatch: pytest.MonkeyPatch,
    db: AsyncSession,
) -> None:
    """A shortened ``request_timeout_seconds``; the scheduler owns the deadline."""
    short = AppSettings.model_construct(
        max_session_minutes=120,
        request_timeout_seconds=1,
        connect_timeout_seconds=30,
        log_domains=False,
        allowed_ports=[80, 443],
    )

    async def fake_get(_: AsyncSession) -> AppSettings:
        return short

    monkeypatch.setattr(request_service.settings_service, "get", fake_get)

    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)

    result = await pair.guest_ws.expect("request.result", timeout=5.0)
    assert (result["accepted"], result["reason"]) == (False, "expired")
    assert (await pair.host_ws.expect("request.expired", timeout=5.0))["request_id"] == request_id

    row = await db.get(ConnectionRequest, uuid.UUID(request_id))
    assert row is not None and row.status == RequestStatus.EXPIRED


# ---------------------------------------------------------------- error codes


async def test_unavailable_host_is_reported_with_the_ref(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    pair = await _pair(ws_connect, make_actor, available=False)
    await pair.guest_ws.send(
        {
            "type": "request.create",
            "ref": "c1",
            "host_device_id": pair.host.device_id,
            "duration_min": 30,
        }
    )
    error = await pair.guest_ws.expect("error")
    assert (error["code"], error["ref"]) == ("host_unavailable", "c1")


async def test_unknown_host_device_is_not_found(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    pair = await _pair(ws_connect, make_actor)
    await pair.guest_ws.send(
        {
            "type": "request.create",
            "ref": "c1",
            "host_device_id": str(uuid.uuid4()),
            "duration_min": 30,
        }
    )
    assert (await pair.guest_ws.expect("error"))["code"] == "not_found"


async def test_duration_is_validated_against_max_session_minutes(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    pair = await _pair(ws_connect, make_actor)
    for duration in (0, 121):
        await pair.guest_ws.send(
            {
                "type": "request.create",
                "ref": "c1",
                "host_device_id": pair.host.device_id,
                "duration_min": duration,
            }
        )
        error = await pair.guest_ws.expect("error")
        assert (error["code"], error["ref"]) == ("bad_request", "c1")


async def test_second_request_from_the_same_guest_is_request_pending(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    pair = await _pair(ws_connect, make_actor)
    await _open_request(pair)
    await pair.guest_ws.send(
        {
            "type": "request.create",
            "ref": "c2",
            "host_device_id": pair.host.device_id,
            "duration_min": 30,
        }
    )
    error = await pair.guest_ws.expect("error")
    assert (error["code"], error["ref"]) == ("request_pending", "c2")


async def test_a_guest_in_a_session_gets_session_exists(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)
    await pair.host_ws.send({"type": "request.accept", "ref": "h1", "request_id": request_id})
    await pair.guest_ws.expect("request.result")
    await pair.guest_ws.expect("session.created")

    await pair.guest_ws.send(
        {
            "type": "request.create",
            "ref": "c9",
            "host_device_id": pair.host.device_id,
            "duration_min": 30,
        }
    )
    error = await pair.guest_ws.expect("error")
    assert (error["code"], error["ref"]) == ("session_exists", "c9")


async def test_only_the_addressed_host_may_answer(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)
    stranger = await make_actor("stranger@example.com", device_name="OTHER-PC")
    stranger_ws = await ws_connect(stranger)

    for message_type in ("request.accept", "request.reject"):
        await stranger_ws.send({"type": message_type, "ref": "x", "request_id": request_id})
        error = await stranger_ws.expect("error")
        assert (error["code"], error["ref"]) == ("forbidden", "x")

    # The guest is the only one who may cancel it, too.
    await pair.host_ws.send({"type": "request.cancel", "ref": "x", "request_id": request_id})
    assert (await pair.host_ws.expect("error"))["code"] == "forbidden"


async def test_answering_a_settled_request_is_not_found(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)
    await pair.host_ws.send({"type": "request.reject", "ref": "h1", "request_id": request_id})
    await pair.guest_ws.expect("request.result")

    await pair.host_ws.send({"type": "request.accept", "ref": "h2", "request_id": request_id})
    error = await pair.host_ws.expect("error")
    assert (error["code"], error["ref"]) == ("not_found", "h2")


async def test_requesting_your_own_device_is_rejected(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    actor = await make_actor("solo@example.com")
    ws = await ws_connect(actor)
    await ws.send({"type": "host.available", "available": True})
    await ws.drain(timeout=0.1)
    await ws.send(
        {
            "type": "request.create",
            "ref": "c1",
            "host_device_id": actor.device_id,
            "duration_min": 30,
        }
    )
    assert (await ws.expect("error"))["code"] == "bad_request"


# ---------------------------------------------------------------- disconnects


async def test_guest_disconnect_cancels_the_pending_request(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)

    await pair.guest_ws.disconnect()

    assert (await pair.host_ws.expect("request.expired"))["request_id"] == request_id
    await db.rollback()
    row = await db.get(ConnectionRequest, uuid.UUID(request_id))
    assert row is not None and row.status == RequestStatus.CANCELLED


async def test_host_disconnect_tells_the_waiting_guest(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)

    await pair.host_ws.disconnect()

    result = await pair.guest_ws.expect("request.result")
    assert (result["request_id"], result["accepted"], result["reason"]) == (
        request_id,
        False,
        "host_unavailable",
    )


async def test_host_disconnect_ends_the_live_session_and_terminates_the_peer(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)
    await pair.host_ws.send({"type": "request.accept", "ref": "h1", "request_id": request_id})
    session_id = (await pair.guest_ws.expect("request.result"))["session_id"]
    await pair.guest_ws.expect("session.created")

    await pair.host_ws.disconnect()

    terminate = await pair.guest_ws.expect("session.terminate")
    assert terminate == {
        "type": "session.terminate",
        "session_id": session_id,
        "reason": "host_disconnected",
    }
    await db.rollback()
    session = await db.get(Session, uuid.UUID(session_id))
    assert session is not None
    assert session.status == SessionStatus.ENDED
    assert session.end_reason == "host_disconnected"
    assert (
        await db.scalar(select(SessionKey.session_id).where(SessionKey.session_id == session.id))
        is None
    )


async def test_guest_disconnect_ends_the_live_session_as_guest_disconnected(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    pair = await _pair(ws_connect, make_actor)
    request_id, _ = await _open_request(pair)
    await pair.host_ws.send({"type": "request.accept", "ref": "h1", "request_id": request_id})
    session_id = (await pair.guest_ws.expect("request.result"))["session_id"]
    await pair.host_ws.expect("session.created")

    await pair.guest_ws.disconnect()

    terminate = await pair.host_ws.expect("session.terminate")
    assert terminate["reason"] == "guest_disconnected"
    await db.rollback()
    session = await db.get(Session, uuid.UUID(session_id))
    assert session is not None and session.end_reason == "guest_disconnected"
