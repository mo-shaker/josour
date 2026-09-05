"""Session lifecycle over the control channel (docs/ws-protocol.md sections 3-5).

Everything here drives real ``/ws`` connections: the endpoint exchange, ``connecting`` ->
``active``, stats, the client-requested ends, and the two server-side deadlines.
"""

import uuid
from dataclasses import dataclass
from datetime import timedelta
from typing import Any

import pytest
from httpx import AsyncClient
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.models import ConnectDiagnostic, Session, SessionDomain, SessionKey
from app.models.enums import SessionStatus, UserRole
from app.schemas.settings import AppSettings, SettingsPatch
from app.services import requests as request_service
from app.services import session_flow
from app.services import sessions as session_service
from app.services.app_settings import settings_service
from app.services.session_timer import scheduler
from tests.conftest import Actor, ActorFactory, SessionFactory, WsFactory
from tests.ws_client import ASGIWebSocket

GUEST_FP = "a" * 64
HOST_FP = "b" * 64
GUEST_CANDIDATES = [{"type": "lan", "ip": "192.168.1.5", "port": 34567}]
HOST_CANDIDATES = [
    {"type": "lan", "ip": "192.168.1.9", "port": 41000},
    {"type": "public", "ip": "198.51.100.9", "port": 41000},
]


@dataclass(slots=True)
class Live:
    """A pair of connected clients whose session is in ``connecting``."""

    guest: Actor
    guest_ws: ASGIWebSocket
    host: Actor
    host_ws: ASGIWebSocket
    session_id: str

    @property
    def uuid(self) -> uuid.UUID:
        return uuid.UUID(self.session_id)


async def _connecting(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    *,
    guest_email: str = "guest@example.com",
    host_email: str = "host@example.com",
    duration_min: int = 30,
) -> Live:
    """Guest asks, host accepts: both hold ``session.created`` and nothing else is pending."""
    guest = await make_actor(guest_email, display_name="Guest", device_name="GUEST-PC")
    host = await make_actor(host_email, display_name="Hosty", device_name="OFFICE-PC")
    guest_ws = await ws_connect(guest)
    host_ws = await ws_connect(host, client=("198.51.100.9", 51000))
    await host_ws.send({"type": "host.available", "available": True})
    await guest_ws.expect("hosts.update", skip=frozenset())
    await guest_ws.drain(timeout=0.1)
    await host_ws.drain(timeout=0.1)

    await guest_ws.send(
        {
            "type": "request.create",
            "ref": "c1",
            "host_device_id": host.device_id,
            "duration_min": duration_min,
        }
    )
    await guest_ws.expect("request.created")
    incoming = await host_ws.expect("request.incoming")
    await host_ws.send(
        {"type": "request.accept", "ref": "h1", "request_id": incoming["request_id"]}
    )
    session_id = (await guest_ws.expect("request.result"))["session_id"]
    await guest_ws.expect("session.created")
    await host_ws.expect("session.created")
    await guest_ws.drain(timeout=0.1)
    await host_ws.drain(timeout=0.1)
    return Live(guest=guest, guest_ws=guest_ws, host=host, host_ws=host_ws, session_id=session_id)


def _endpoint(session_id: str, fp: str, candidates: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "type": "session.endpoint",
        "session_id": session_id,
        "cert_fp_sha256": fp,
        "candidates": candidates,
    }


def _connected(session_id: str, **overrides: Any) -> dict[str, Any]:
    return {
        "type": "session.connected",
        "session_id": session_id,
        "winner_type": "lan",
        "connect_ms": 187,
        "tls_version": "1.3",
        **overrides,
    }


def _end(session_id: str, reason: str, **overrides: Any) -> dict[str, Any]:
    return {
        "type": "session.end",
        "session_id": session_id,
        "reason": reason,
        "bytes_up": 0,
        "bytes_down": 0,
        "domains": [],
        **overrides,
    }


async def _fresh(db: AsyncSession) -> None:
    """The services commit in their own sessions; drop anything this one still remembers."""
    await db.rollback()
    db.expire_all()


async def _reload(db: AsyncSession, session_id: uuid.UUID) -> Session:
    await _fresh(db)
    session = await db.get(Session, session_id)
    assert session is not None
    return session


async def _keys(db: AsyncSession, session_id: uuid.UUID) -> uuid.UUID | None:
    await _fresh(db)
    return await db.scalar(select(SessionKey.session_id).where(SessionKey.session_id == session_id))


def _short_settings(**overrides: Any) -> AppSettings:
    """Below the ``ge=10`` floors the operator API enforces, so the timers fire inside a test."""
    values: dict[str, Any] = {
        "max_session_minutes": 120,
        "request_timeout_seconds": 60,
        "connect_timeout_seconds": 30,
        "log_domains": False,
        "allowed_ports": [80, 443],
    }
    return AppSettings.model_construct(**(values | overrides))


# ---------------------------------------------------------------- endpoint exchange


async def test_endpoint_exchange_guest_first(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)

    await live.guest_ws.send(_endpoint(live.session_id, GUEST_FP, GUEST_CANDIDATES))
    forwarded = await live.host_ws.expect("session.peer_endpoint")
    assert forwarded == {
        "type": "session.peer_endpoint",
        "session_id": live.session_id,
        "cert_fp_sha256": GUEST_FP,
        "candidates": GUEST_CANDIDATES,
    }
    # Nothing comes back to the guest: the host has not said anything yet.
    assert await live.guest_ws.drain(timeout=0.1) == []

    await live.host_ws.send(_endpoint(live.session_id, HOST_FP, HOST_CANDIDATES))
    to_guest = await live.guest_ws.expect("session.peer_endpoint")
    assert (to_guest["cert_fp_sha256"], to_guest["candidates"]) == (HOST_FP, HOST_CANDIDATES)
    # The host also gets the guest's stored endpoint back, so ordering cannot matter.
    echoed = await live.host_ws.expect("session.peer_endpoint")
    assert (echoed["cert_fp_sha256"], echoed["candidates"]) == (GUEST_FP, GUEST_CANDIDATES)

    await _fresh(db)
    key = await db.get(SessionKey, live.uuid)
    assert key is not None
    assert (key.guest_cert_fp, key.host_cert_fp) == (GUEST_FP, HOST_FP)
    assert key.guest_candidates == GUEST_CANDIDATES
    assert key.host_candidates == HOST_CANDIDATES


async def test_endpoint_exchange_host_first(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    live = await _connecting(ws_connect, make_actor)

    await live.host_ws.send(_endpoint(live.session_id, HOST_FP, HOST_CANDIDATES))
    first = await live.guest_ws.expect("session.peer_endpoint")
    assert first["cert_fp_sha256"] == HOST_FP
    assert await live.host_ws.drain(timeout=0.1) == []

    await live.guest_ws.send(_endpoint(live.session_id, GUEST_FP, GUEST_CANDIDATES))
    assert (await live.host_ws.expect("session.peer_endpoint"))["cert_fp_sha256"] == GUEST_FP
    echoed = await live.guest_ws.expect("session.peer_endpoint")
    assert (echoed["cert_fp_sha256"], echoed["candidates"]) == (HOST_FP, HOST_CANDIDATES)


async def test_a_late_party_still_receives_the_peer_endpoint(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    """The host misses the forwarded frame entirely; sending its own endpoint is enough."""
    live = await _connecting(ws_connect, make_actor)
    await live.guest_ws.send(_endpoint(live.session_id, GUEST_FP, GUEST_CANDIDATES))
    assert [f["type"] for f in await live.host_ws.drain(timeout=0.2)] == ["session.peer_endpoint"]

    await live.host_ws.send(_endpoint(live.session_id, HOST_FP, HOST_CANDIDATES))

    peer = await live.host_ws.expect("session.peer_endpoint")
    assert (peer["cert_fp_sha256"], peer["candidates"]) == (GUEST_FP, GUEST_CANDIDATES)


async def test_a_second_endpoint_replaces_the_stored_one(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)
    replacement = [{"type": "upnp", "ip": "203.0.113.10", "port": 50000}]

    await live.guest_ws.send(_endpoint(live.session_id, GUEST_FP, GUEST_CANDIDATES))
    await live.host_ws.expect("session.peer_endpoint")
    await live.guest_ws.send(_endpoint(live.session_id, HOST_FP, replacement))
    again = await live.host_ws.expect("session.peer_endpoint")
    assert (again["cert_fp_sha256"], again["candidates"]) == (HOST_FP, replacement)

    await _fresh(db)
    key = await db.get(SessionKey, live.uuid)
    assert key is not None
    assert (key.guest_cert_fp, key.guest_candidates) == (HOST_FP, replacement)

    # And the replacement is what the peer gets when it answers.
    await live.host_ws.send(_endpoint(live.session_id, HOST_FP, HOST_CANDIDATES))
    await live.host_ws.expect("session.peer_endpoint")
    echoed = await live.guest_ws.expect("session.peer_endpoint")
    assert echoed["candidates"] == HOST_CANDIDATES


async def test_endpoint_after_the_session_is_active_is_bad_request(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.host_ws.send(_connected(live.session_id))
    await live.host_ws.expect("session.active")
    await live.guest_ws.expect("session.active")

    await live.guest_ws.send(_endpoint(live.session_id, GUEST_FP, GUEST_CANDIDATES))
    assert (await live.guest_ws.expect("error"))["code"] == "bad_request"


# ---------------------------------------------------------------- authorisation


async def test_a_stranger_cannot_touch_the_session(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    live = await _connecting(ws_connect, make_actor)
    stranger = await make_actor("stranger@example.com", device_name="OTHER-PC")
    stranger_ws = await ws_connect(stranger)
    await stranger_ws.drain(timeout=0.1)

    frames = [
        _endpoint(live.session_id, GUEST_FP, GUEST_CANDIDATES),
        _connected(live.session_id),
        {"type": "session.stats", "session_id": live.session_id, "bytes_up": 1, "bytes_down": 2},
        _end(live.session_id, "guest_ended"),
        {"type": "session.connect_failed", "session_id": live.session_id, "diagnostics": {}},
    ]
    for frame in frames:
        await stranger_ws.send(frame)
        error = await stranger_ws.expect("error")
        assert error["code"] == "forbidden", frame["type"]

    quiet = [f for f in await live.guest_ws.drain(timeout=0.1) if f["type"] != "hosts.update"]
    assert quiet == []


async def test_an_unknown_session_is_not_found(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.guest_ws.send(_endpoint(str(uuid.uuid4()), GUEST_FP, GUEST_CANDIDATES))
    assert (await live.guest_ws.expect("error"))["code"] == "not_found"


async def test_session_connected_from_the_guest_is_forbidden(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    """Section 5: the guest never decides that the tunnel is up."""
    live = await _connecting(ws_connect, make_actor)

    await live.guest_ws.send(_connected(live.session_id))

    error = await live.guest_ws.expect("error")
    assert (error["code"], error["ref"]) == ("forbidden", None)
    assert (await _reload(db, live.uuid)).status == SessionStatus.CONNECTING


# ---------------------------------------------------------------- connecting -> active


async def test_session_connected_activates_and_tells_both_parties(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)

    await live.host_ws.send(_connected(live.session_id, winner_type="upnp", tls_version="1.2"))

    for ws in (live.guest_ws, live.host_ws):
        active = await ws.expect("session.active")
        assert active["session_id"] == live.session_id
        assert active["expires_at"].endswith("Z")

    session = await _reload(db, live.uuid)
    assert session.status == SessionStatus.ACTIVE
    assert session.started_at is not None
    assert (session.connect_result, session.winner_type) == ("ok", "upnp")
    assert (session.tls_version, session.connect_ms) == ("1.2", 187)
    # The connect deadline is gone, the expiry timer remains.
    assert not scheduler.is_scheduled(session_service.connect_timer_key(live.uuid))
    assert scheduler.is_scheduled(session_service.expiry_timer_key(live.uuid))


async def test_session_connected_twice_is_bad_request(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.host_ws.send(_connected(live.session_id))
    await live.host_ws.expect("session.active")
    await live.guest_ws.expect("session.active")

    await live.host_ws.send(_connected(live.session_id))
    assert (await live.host_ws.expect("error"))["code"] == "bad_request"


async def test_the_connect_deadline_ends_the_session(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    monkeypatch: pytest.MonkeyPatch,
    db: AsyncSession,
) -> None:
    """A shortened ``connect_timeout_seconds``; nobody ever reports ``session.connected``."""

    async def fake_get(_: AsyncSession) -> AppSettings:
        return _short_settings(connect_timeout_seconds=1)

    monkeypatch.setattr(request_service.settings_service, "get", fake_get)
    live = await _connecting(ws_connect, make_actor)

    for ws in (live.guest_ws, live.host_ws):
        terminate = await ws.expect("session.terminate", timeout=5.0)
        assert terminate["reason"] == "connect_failed"

    session = await _reload(db, live.uuid)
    assert (session.status, session.end_reason) == (SessionStatus.ENDED, "connect_failed")
    assert session.connect_result == "timeout"
    assert await _keys(db, live.uuid) is None
    assert len(scheduler) == 0


# ---------------------------------------------------------------- connect failure


async def test_connect_failed_records_diagnostics_and_ends_the_session(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)
    guest_device_id = live.guest.device.id
    report = {"tried": [{"type": "lan", "ms": 3000, "error": "timeout"}], "upnp_found": False}

    await live.guest_ws.send(
        {
            "type": "session.connect_failed",
            "session_id": live.session_id,
            "diagnostics": report,
        }
    )

    for ws in (live.guest_ws, live.host_ws):
        assert (await ws.expect("session.terminate"))["reason"] == "connect_failed"

    session = await _reload(db, live.uuid)
    assert (session.status, session.end_reason) == (SessionStatus.ENDED, "connect_failed")
    assert session.connect_result == "failed"
    assert await _keys(db, live.uuid) is None

    row = await db.scalar(
        select(ConnectDiagnostic).where(ConnectDiagnostic.session_id == live.uuid)
    )
    assert row is not None
    assert (row.device_id, row.role) == (guest_device_id, "guest")
    assert row.data == report


async def test_oversized_connect_diagnostics_are_dropped_but_the_session_still_ends(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    db: AsyncSession,
    caplog: pytest.LogCaptureFixture,
) -> None:
    live = await _connecting(ws_connect, make_actor)

    with caplog.at_level("WARNING", logger="app.services.session_flow"):
        await live.host_ws.send(
            {
                "type": "session.connect_failed",
                "session_id": live.session_id,
                "diagnostics": {"blob": "x" * (64 * 1024 + 1)},
            }
        )
        assert (await live.guest_ws.expect("session.terminate"))["reason"] == "connect_failed"

    assert any("connect diagnostics dropped" in record.message for record in caplog.records)
    await _fresh(db)
    assert await db.scalar(select(func.count()).select_from(ConnectDiagnostic)) == 0
    assert (await _reload(db, live.uuid)).end_reason == "connect_failed"


# ---------------------------------------------------------------- stats


async def test_stats_update_the_session_and_never_go_backwards(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.host_ws.send(_connected(live.session_id))
    await live.host_ws.expect("session.active")
    await live.guest_ws.expect("session.active")

    await live.host_ws.send(
        {
            "type": "session.stats",
            "session_id": live.session_id,
            "bytes_up": 1_000,
            "bytes_down": 2_000,
        }
    )
    await live.host_ws.send(
        {
            "type": "session.stats",
            "session_id": live.session_id,
            "bytes_up": 1_500,
            "bytes_down": 2_500,
        }
    )
    # A client restart resets its own counters; the stored totals must not shrink.
    await live.host_ws.send(
        {"type": "session.stats", "session_id": live.session_id, "bytes_up": 10, "bytes_down": 20}
    )
    assert await live.host_ws.drain(timeout=0.2) == [], "stats are not echoed anywhere"

    session = await _reload(db, live.uuid)
    assert (session.bytes_up, session.bytes_down) == (1_500, 2_500)


async def test_stats_from_the_guest_are_forbidden_and_before_active_bad_request(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    live = await _connecting(ws_connect, make_actor)
    stats = {"type": "session.stats", "session_id": live.session_id, "bytes_up": 1, "bytes_down": 1}

    await live.host_ws.send(stats)
    assert (await live.host_ws.expect("error"))["code"] == "bad_request"

    await live.guest_ws.send(stats)
    assert (await live.guest_ws.expect("error"))["code"] == "forbidden"


# ---------------------------------------------------------------- session.end


@pytest.mark.parametrize(
    ("sender", "reason"),
    [("guest", "guest_ended"), ("host", "host_ended"), ("guest", "browser_not_proxied")],
)
async def test_session_end_from_either_party(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    db: AsyncSession,
    sender: str,
    reason: str,
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.host_ws.send(_connected(live.session_id))
    await live.host_ws.expect("session.active")
    await live.guest_ws.expect("session.active")

    ws = live.guest_ws if sender == "guest" else live.host_ws
    await ws.send(_end(live.session_id, reason, bytes_up=4_096, bytes_down=8_192))

    for each in (live.guest_ws, live.host_ws):
        assert (await each.expect("session.terminate"))["reason"] == reason

    session = await _reload(db, live.uuid)
    assert (session.status, session.end_reason) == (SessionStatus.ENDED, reason)
    assert (session.bytes_up, session.bytes_down) == (4_096, 8_192)
    assert await _keys(db, live.uuid) is None
    assert len(scheduler) == 0


async def test_final_bytes_are_monotonic_too(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.host_ws.send(_connected(live.session_id))
    await live.host_ws.expect("session.active")
    await live.guest_ws.expect("session.active")
    # Both frames on the host's connection, so they are dispatched in order.
    await live.host_ws.send(
        {"type": "session.stats", "session_id": live.session_id, "bytes_up": 900, "bytes_down": 800}
    )

    await live.host_ws.send(_end(live.session_id, "host_ended", bytes_up=5, bytes_down=900))
    await live.host_ws.expect("session.terminate")

    session = await _reload(db, live.uuid)
    assert (session.bytes_up, session.bytes_down) == (900, 900)


async def test_the_end_reason_must_match_the_sender_role(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)

    await live.guest_ws.send(_end(live.session_id, "host_ended"))
    assert (await live.guest_ws.expect("error"))["code"] == "bad_request"
    await live.host_ws.send(_end(live.session_id, "guest_ended"))
    assert (await live.host_ws.expect("error"))["code"] == "bad_request"

    assert (await _reload(db, live.uuid)).status == SessionStatus.CONNECTING


async def test_session_end_is_accepted_while_still_connecting(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)

    await live.host_ws.send(_end(live.session_id, "protocol_error"))

    assert (await live.guest_ws.expect("session.terminate"))["reason"] == "protocol_error"
    assert (await _reload(db, live.uuid)).end_reason == "protocol_error"


async def test_ending_an_already_ended_session_is_not_found(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.guest_ws.send(_end(live.session_id, "guest_ended"))
    await live.guest_ws.expect("session.terminate")

    await live.guest_ws.send(_end(live.session_id, "guest_ended"))
    assert (await live.guest_ws.expect("error"))["code"] == "not_found"


# ---------------------------------------------------------------- domains


async def test_domains_are_not_stored_while_logging_is_off(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)

    await live.guest_ws.send(
        _end(live.session_id, "guest_ended", domains=["example.com", "intranet.corp"])
    )
    await live.guest_ws.expect("session.terminate")

    await _fresh(db)
    assert await db.scalar(select(func.count()).select_from(SessionDomain)) == 0


async def test_domains_are_stored_lowercased_and_deduplicated_when_logging_is_on(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    await settings_service.update(db, SettingsPatch(log_domains=True))
    live = await _connecting(ws_connect, make_actor)

    await live.guest_ws.send(
        _end(
            live.session_id,
            "guest_ended",
            domains=["Example.COM", "example.com", " intranet.corp. ", "", "bad host"],
        )
    )
    await live.guest_ws.expect("session.terminate")

    await _fresh(db)
    rows = {
        row.domain: row.hit_count
        for row in await db.scalars(
            select(SessionDomain).where(SessionDomain.session_id == live.uuid)
        )
    }
    assert rows == {"example.com": 2, "intranet.corp": 1}


async def test_domains_are_capped_per_session(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    await settings_service.update(db, SettingsPatch(log_domains=True))
    live = await _connecting(ws_connect, make_actor)

    await live.guest_ws.send(
        _end(live.session_id, "guest_ended", domains=[f"host{i}.example.com" for i in range(260)])
    )
    await live.guest_ws.expect("session.terminate")

    await _fresh(db)
    stored = await db.scalar(
        select(func.count()).select_from(SessionDomain).where(SessionDomain.session_id == live.uuid)
    )
    assert stored == session_flow.MAX_DOMAINS_PER_SESSION


# ---------------------------------------------------------------- timers


async def test_accepting_arms_both_timers_and_ending_early_cancels_them(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)
    assert scheduler.is_scheduled(session_service.connect_timer_key(live.uuid))
    assert scheduler.is_scheduled(session_service.expiry_timer_key(live.uuid))

    await live.guest_ws.send(_end(live.session_id, "guest_ended"))
    await live.guest_ws.expect("session.terminate")

    assert len(scheduler) == 0, "no timer may outlive the session it belongs to"
    # And nothing fires late: the end reason stays what the client asked for.
    assert [f["type"] for f in await live.guest_ws.drain(timeout=0.3)] == ["hosts.update"]
    assert (await _reload(db, live.uuid)).end_reason == "guest_ended"


async def test_the_expiry_timer_ends_an_active_session(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.host_ws.send(_connected(live.session_id))
    await live.host_ws.expect("session.active")
    await live.guest_ws.expect("session.active")

    # Re-arm the expiry at a deadline a test can wait for.
    session = await _reload(db, live.uuid)
    session.expires_at = utcnow() + timedelta(seconds=0.2)
    await db.commit()
    session_flow.schedule_timers(session, connect_timeout_seconds=30)

    for ws in (live.guest_ws, live.host_ws):
        assert (await ws.expect("session.terminate", timeout=5.0))["reason"] == "expired"

    reloaded = await _reload(db, live.uuid)
    assert (reloaded.status, reloaded.end_reason) == (SessionStatus.ENDED, "expired")
    assert await _keys(db, live.uuid) is None
    assert len(scheduler) == 0


async def test_the_expiry_timer_also_ends_a_session_stuck_in_connecting(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)
    session = await _reload(db, live.uuid)
    session.expires_at = utcnow() + timedelta(seconds=0.2)
    await db.commit()
    session_flow.schedule_timers(session, connect_timeout_seconds=3600)

    assert (await live.guest_ws.expect("session.terminate", timeout=5.0))["reason"] == "expired"
    assert (await _reload(db, live.uuid)).end_reason == "expired"


async def test_a_timer_that_fires_after_the_session_ended_is_a_no_op(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    """The callbacks are guarded by the session state, not only by the cancellation."""
    live = await _connecting(ws_connect, make_actor)
    await live.guest_ws.send(_end(live.session_id, "guest_ended"))
    await live.guest_ws.expect("session.terminate")

    assert await session_flow.expire_session(live.uuid) is False
    assert await session_flow.expire_connecting(live.uuid) is False
    assert await session_flow.expire_session(uuid.uuid4()) is False
    assert (await _reload(db, live.uuid)).end_reason == "guest_ended"


async def test_startup_leaves_no_live_session_and_no_session_timer(
    make_actor: ActorFactory,
    make_session: SessionFactory,
    db: AsyncSession,
    run_lifespan: Any,
) -> None:
    """``end_dangling_sessions`` and ``reschedule_timers`` must agree: nothing live, no timers."""
    guest = await make_actor("guest9@example.com")
    host = await make_actor("host9@example.com", device_name="OFFICE-PC")
    session = await make_session(
        guest.user, guest.device, host.user, host.device, status=SessionStatus.CONNECTING
    )

    async with run_lifespan():
        assert (await _reload(db, session.id)).status == SessionStatus.ENDED
        assert (
            await db.scalar(
                select(func.count())
                .select_from(Session)
                .where(Session.status.in_(("connecting", "active")))
            )
            == 0
        )
        assert len(scheduler) == 0


# ---------------------------------------------------------------- every ending path


async def _end_via_client(live: Live, **_: Any) -> str:
    await live.guest_ws.send(_end(live.session_id, "guest_ended"))
    return "guest_ended"


async def _end_via_disconnect(live: Live, **_: Any) -> str:
    await live.guest_ws.disconnect()
    return "guest_disconnected"


async def _end_via_connect_failed(live: Live, **_: Any) -> str:
    await live.host_ws.send(
        {"type": "session.connect_failed", "session_id": live.session_id, "diagnostics": {}}
    )
    return "connect_failed"


async def _end_via_connect_deadline(live: Live, *, db: AsyncSession, **_: Any) -> str:
    session = await _reload(db, live.uuid)
    session_flow.schedule_timers(session, connect_timeout_seconds=0)
    return "connect_failed"


async def _end_via_expiry(live: Live, *, db: AsyncSession, **_: Any) -> str:
    session = await _reload(db, live.uuid)
    session.expires_at = utcnow() + timedelta(seconds=0.1)
    await db.commit()
    session_flow.schedule_timers(session, connect_timeout_seconds=3600)
    return "expired"


async def _end_via_admin(live: Live, *, client: AsyncClient, admin: Actor, **_: Any) -> str:
    response = await client.post(
        f"/api/v1/admin/sessions/{live.session_id}/terminate",
        headers={"Authorization": f"Bearer {admin.token}"},
    )
    assert response.status_code == 204, response.text
    return "admin_terminated"


END_PATHS = {
    "client_end": _end_via_client,
    "disconnect": _end_via_disconnect,
    "connect_failed": _end_via_connect_failed,
    "connect_deadline": _end_via_connect_deadline,
    "expiry": _end_via_expiry,
    "admin_terminate": _end_via_admin,
}


@pytest.mark.parametrize("path", sorted(END_PATHS))
async def test_every_ending_path_deletes_the_keys_and_terminates_the_peer(
    ws_connect: WsFactory,
    make_actor: ActorFactory,
    client: AsyncClient,
    db: AsyncSession,
    path: str,
) -> None:
    """One funnel: ``sessions.end_session``. Whatever ends a session, the secret is gone, the
    peer is told, and no timer is left behind."""
    admin = await make_actor("admin@example.com", display_name="Admin", role=UserRole.ADMIN)
    live = await _connecting(ws_connect, make_actor)
    assert await _keys(db, live.uuid) is not None

    reason = await END_PATHS[path](live, db=db, client=client, admin=admin)

    terminate = await live.host_ws.expect("session.terminate", timeout=5.0)
    assert terminate == {
        "type": "session.terminate",
        "session_id": live.session_id,
        "reason": reason,
    }
    session = await _reload(db, live.uuid)
    assert (session.status, session.end_reason) == (SessionStatus.ENDED, reason)
    assert await _keys(db, live.uuid) is None, f"{path} left the session_keys row behind"
    assert len(scheduler) == 0, f"{path} left a timer behind"


# ---------------------------------------------------------------- admin summary


async def test_a_completed_session_shows_up_in_the_diagnostics_summary(
    ws_connect: WsFactory, make_actor: ActorFactory, client: AsyncClient
) -> None:
    """``GET /admin/diagnostics`` summarises the columns this flow now fills in."""
    admin = await make_actor("admin2@example.com", display_name="Admin", role=UserRole.ADMIN)
    live = await _connecting(ws_connect, make_actor)
    await live.host_ws.send(_connected(live.session_id, winner_type="public"))
    await live.host_ws.expect("session.active")
    await live.guest_ws.expect("session.active")
    await live.guest_ws.send(_end(live.session_id, "guest_ended"))
    await live.guest_ws.expect("session.terminate")

    response = await client.get(
        "/api/v1/admin/diagnostics", headers={"Authorization": f"Bearer {admin.token}"}
    )
    assert response.status_code == 200, response.text
    assert response.json() == {
        "total_sessions": 1,
        "sessions_with_connect_result": 1,
        "connect_ok_ratio": 1.0,
        "winner_type_distribution": {"public": 1},
        "tls_version_distribution": {"1.3": 1},
        "connect_diagnostics_count": 0,
        "end_reason_distribution": {"guest_ended": 1},
    }


# --- تصحيح تكامل: العميل هو المصدر الوحيد لخبر موت النفق ---
# إن مات النفق بينما تبقى قناتا التحكم حيتين، لا يستطيع الخادم ملاحظة ذلك بنفسه،
# فيقبل تقرير الطرف عن اختفاء نظيره. ويظل يرفض ادعاءه اختفاء نفسه وادعاء أحكام الخادم.


async def test_host_reports_a_dead_tunnel_as_guest_disconnected(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.host_ws.send(_connected(live.session_id))
    await live.host_ws.expect("session.active")
    await live.guest_ws.expect("session.active")

    await live.host_ws.send(_end(live.session_id, "guest_disconnected"))
    terminate = await live.guest_ws.expect("session.terminate")

    assert terminate["reason"] == "guest_disconnected"
    assert (await _reload(db, live.uuid)).end_reason == "guest_disconnected"
    assert await db.scalar(select(func.count()).select_from(SessionKey)) == 0


async def test_guest_reports_a_dead_tunnel_as_host_disconnected(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.guest_ws.send(_end(live.session_id, "host_disconnected"))
    terminate = await live.host_ws.expect("session.terminate")

    assert terminate["reason"] == "host_disconnected"
    assert (await _reload(db, live.uuid)).end_reason == "host_disconnected"


async def test_a_party_may_not_claim_its_own_disconnect(
    ws_connect: WsFactory, make_actor: ActorFactory
) -> None:
    live = await _connecting(ws_connect, make_actor)
    await live.host_ws.send(_end(live.session_id, "host_disconnected"))
    error = await live.host_ws.expect("error")
    assert error["code"] == "bad_request"


@pytest.mark.parametrize("reason", ["expired", "connect_failed", "admin_terminated"])
async def test_server_verdicts_are_refused_from_clients(
    ws_connect: WsFactory, make_actor: ActorFactory, reason: str
) -> None:
    """المدة والفشل والإنهاء المركزي يرصدها الخادم بنفسه؛ ادعاؤها من عميل مرفوض."""
    live = await _connecting(ws_connect, make_actor)
    await live.guest_ws.send(_end(live.session_id, reason))
    error = await live.guest_ws.expect("error")
    assert error["code"] == "bad_request"


async def test_every_session_gets_a_fresh_key_and_none_survives_the_end(
    ws_connect: WsFactory, make_actor: ActorFactory, db: AsyncSession
) -> None:
    """Product document section 14: session keys are never reused, and are revoked at the end.

    Two sessions in a row between the same two devices must not share key material, and neither
    ``session_keys`` row may outlive its session."""
    secrets_seen: list[str] = []
    for round_number in (1, 2):
        live = await _connecting(
            ws_connect,
            make_actor,
            guest_email=f"g{round_number}@example.com",
            host_email=f"h{round_number}@example.com",
        )
        stored = await db.get(SessionKey, live.uuid)
        assert stored is not None
        assert len(stored.secret) == 32, "32 raw bytes (docs/ws-protocol.md section 4)"
        secrets_seen.append(stored.secret.hex())

        await live.guest_ws.send(_end(live.session_id, "guest_ended"))
        await live.guest_ws.expect("session.terminate")
        db.expire_all()
        assert await db.get(SessionKey, live.uuid) is None

    assert secrets_seen[0] != secrets_seen[1], "a session key must never be reused"
    assert await db.scalar(select(func.count()).select_from(SessionKey)) == 0
