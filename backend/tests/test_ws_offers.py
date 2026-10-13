"""Offers: a host offering its connection to a contact (ADR-0014, docs/ws-protocol.md 5b)."""

import base64
import uuid
from dataclasses import dataclass

import pytest
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import ConnectionRequest, Device, Session
from app.models.enums import RequestInitiator, RequestStatus, SessionStatus
from app.schemas.settings import AppSettings
from app.services import offers as offer_service
from tests.conftest import Actor, ActorFactory, WsFactory
from tests.ws_client import ASGIWebSocket

CAPS = ["contacts", "offers", "trust_v2"]
NOISE = frozenset({"hosts.update", "ping", "contacts.presence", "host.state"})


@dataclass(slots=True)
class Scene:
    host: Actor
    host_ws: ASGIWebSocket
    guest: Actor
    phone: Device
    """A second device of the guest's."""
    laptop_ws: ASGIWebSocket
    phone_ws: ASGIWebSocket


async def _scene(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    db: AsyncSession,
    access_token,
    *,
    phone_caps: list[str] | None = None,
) -> Scene:
    host = await make_actor("host@example.com", display_name="Hosty", device_name="OFFICE-PC")
    guest = await make_actor("guest@example.com", display_name="Guest", device_name="LAPTOP")
    phone = Device(user_id=guest.user.id, name="PHONE", os_version="macOS", device_secret_hash="x")
    db.add(phone)
    await db.commit()
    phone_actor = Actor(user=guest.user, device=phone, token=access_token(guest.user, phone))
    host_ws = await ws_connect(host, capabilities=CAPS)
    laptop_ws = await ws_connect(guest, capabilities=CAPS)
    phone_ws = await ws_connect(
        phone_actor, capabilities=CAPS if phone_caps is None else phone_caps
    )
    for ws in (host_ws, laptop_ws, phone_ws):
        await ws.drain(timeout=0.1)
    return Scene(host, host_ws, guest, phone, laptop_ws, phone_ws)


async def _offer(scene: Scene, *, duration_min: int = 30) -> str:
    await scene.host_ws.send(
        {
            "type": "offer.create",
            "ref": "o1",
            "guest_user_id": scene.guest.user_id,
            "duration_min": duration_min,
        }
    )
    created = await scene.host_ws.expect("offer.created", skip=NOISE)
    assert created["ref"] == "o1"
    return created["offer_id"]


async def test_an_offer_rings_every_capable_device_of_the_guest(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession, access_token
) -> None:
    scene = await _scene(ws_connect, make_actor, db, access_token)
    offer_id = await _offer(scene, duration_min=45)

    for ws in (scene.laptop_ws, scene.phone_ws):
        incoming = await ws.expect("offer.incoming", skip=NOISE)
        assert incoming["offer_id"] == offer_id
        assert (incoming["host_user_id"], incoming["host_device_id"]) == (
            scene.host.user_id,
            scene.host.device_id,
        )
        assert (incoming["host_name"], incoming["host_device"]) == ("Hosty", "OFFICE-PC")
        assert incoming["duration_min"] == 45

    row = await db.get(ConnectionRequest, uuid.UUID(offer_id))
    assert row is not None
    assert (row.initiator, row.status, row.guest_device_id) == (
        RequestInitiator.HOST,
        RequestStatus.PENDING,
        None,
    )


async def test_the_first_device_to_accept_takes_the_session(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession, access_token
) -> None:
    scene = await _scene(ws_connect, make_actor, db, access_token)
    offer_id = await _offer(scene)
    await scene.laptop_ws.expect("offer.incoming", skip=NOISE)
    await scene.phone_ws.expect("offer.incoming", skip=NOISE)

    await scene.phone_ws.send({"type": "offer.accept", "ref": "p1", "offer_id": offer_id})

    result = await scene.host_ws.expect("offer.result", skip=NOISE)
    assert result["accepted"] is True
    session_id = result["session_id"]
    closed = await scene.laptop_ws.expect("offer.closed", skip=NOISE)
    assert closed == {"type": "offer.closed", "offer_id": offer_id, "reason": "accepted_elsewhere"}

    guest_created = await scene.phone_ws.expect("session.created", skip=NOISE)
    host_created = await scene.host_ws.expect("session.created", skip=NOISE)
    assert guest_created["session_id"] == host_created["session_id"] == session_id
    assert (guest_created["role"], host_created["role"]) == ("guest", "host")
    assert guest_created["initiator"] == host_created["initiator"] == "host"
    assert len(base64.b64decode(guest_created["secret_b64"])) == 32
    assert host_created["peer"]["device_id"] == str(scene.phone.id)

    session = await db.get(Session, uuid.UUID(session_id))
    assert session is not None and session.status == SessionStatus.CONNECTING
    assert session.guest_device_id == scene.phone.id

    # The laptop arrives late: the offer is gone.
    await scene.laptop_ws.send({"type": "offer.accept", "ref": "l1", "offer_id": offer_id})
    assert (await scene.laptop_ws.expect("error", skip=NOISE))["code"] == "not_found"


async def test_rejecting_closes_the_offer_everywhere(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession, access_token
) -> None:
    scene = await _scene(ws_connect, make_actor, db, access_token)
    offer_id = await _offer(scene)
    await scene.laptop_ws.expect("offer.incoming", skip=NOISE)

    await scene.laptop_ws.send({"type": "offer.reject", "ref": "l1", "offer_id": offer_id})
    result = await scene.host_ws.expect("offer.result", skip=NOISE)
    assert (result["accepted"], result["reason"]) == (False, "rejected")
    for ws in (scene.laptop_ws, scene.phone_ws):
        closed = await ws.expect("offer.closed", skip=NOISE | {"offer.incoming"})
        assert closed["reason"] == "rejected"


async def test_the_host_can_withdraw_its_offer(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession, access_token
) -> None:
    scene = await _scene(ws_connect, make_actor, db, access_token)
    offer_id = await _offer(scene)
    # Only the offering device may withdraw it.
    await scene.laptop_ws.send({"type": "offer.cancel", "ref": "x", "offer_id": offer_id})
    assert (await scene.laptop_ws.expect("error", skip=NOISE | {"offer.incoming"}))[
        "code"
    ] == "forbidden"

    await scene.host_ws.send({"type": "offer.cancel", "ref": "h2", "offer_id": offer_id})
    assert (await scene.host_ws.expect("offer.result", skip=NOISE))["reason"] == "cancelled"
    closed = await scene.phone_ws.expect("offer.closed", skip=NOISE | {"offer.incoming"})
    assert closed["reason"] == "cancelled"


async def test_a_device_without_the_capability_is_not_offered_anything(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession, access_token
) -> None:
    scene = await _scene(ws_connect, make_actor, db, access_token, phone_caps=[])
    await _offer(scene)
    await scene.laptop_ws.expect("offer.incoming", skip=NOISE)
    assert all(f["type"] != "offer.incoming" for f in await scene.phone_ws.drain(timeout=0.2))


async def test_an_offer_needs_a_contact_with_a_capable_device(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    host = await make_actor("host@example.com")
    stranger = await make_actor("stranger@example.com", contacts=False)
    old_client = await make_actor("old@example.com")
    host_ws = await ws_connect(host, capabilities=CAPS)
    await ws_connect(stranger, capabilities=CAPS)
    await ws_connect(old_client)  # online, but cannot take offers

    for target, code in (
        (stranger, "not_contact"),
        (old_client, "guest_unavailable"),
        (host, "bad_request"),
    ):
        await host_ws.send(
            {
                "type": "offer.create",
                "ref": f"o-{code}",
                "guest_user_id": target.user_id,
                "duration_min": 30,
            }
        )
        error = await host_ws.expect("error", skip=NOISE)
        assert (error["code"], error["ref"]) == (code, f"o-{code}")


async def test_an_offer_expires_on_its_own(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    db: AsyncSession,
    access_token,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    short = AppSettings.model_construct(
        max_session_minutes=120,
        request_timeout_seconds=1,
        connect_timeout_seconds=30,
        log_domains=False,
        allowed_ports=[80, 443],
    )

    async def fake_get(_: AsyncSession) -> AppSettings:
        return short

    monkeypatch.setattr(offer_service.settings_service, "get", fake_get)
    scene = await _scene(ws_connect, make_actor, db, access_token)
    offer_id = await _offer(scene)

    result = await scene.host_ws.expect("offer.result", skip=NOISE, timeout=5.0)
    assert (result["accepted"], result["reason"]) == (False, "expired")
    closed = await scene.phone_ws.expect("offer.closed", skip=NOISE | {"offer.incoming"})
    assert closed["reason"] == "expired"
    row = await db.get(ConnectionRequest, uuid.UUID(offer_id))
    assert row is not None and row.status == RequestStatus.EXPIRED


async def test_the_host_leaving_withdraws_its_offer(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession, access_token
) -> None:
    scene = await _scene(ws_connect, make_actor, db, access_token)
    offer_id = await _offer(scene)
    await scene.host_ws.disconnect()
    closed = await scene.laptop_ws.expect("offer.closed", skip=NOISE | {"offer.incoming"})
    assert (closed["offer_id"], closed["reason"]) == (offer_id, "host_unavailable")


async def test_an_offer_cannot_be_answered_with_request_frames(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession, access_token
) -> None:
    """``request.accept`` is the host answering a guest; the host must not accept its own
    offer through it."""
    scene = await _scene(ws_connect, make_actor, db, access_token)
    offer_id = await _offer(scene)
    await scene.host_ws.send({"type": "request.accept", "ref": "h9", "request_id": offer_id})
    assert (await scene.host_ws.expect("error", skip=NOISE))["code"] == "not_found"
    row = await db.get(ConnectionRequest, uuid.UUID(offer_id))
    assert row is not None and row.status == RequestStatus.PENDING
