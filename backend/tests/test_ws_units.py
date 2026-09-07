"""Unit coverage for the WebSocket building blocks: protocol, registry and scheduler."""

import asyncio
import json
import uuid
from datetime import timedelta
from typing import Any

import pytest
from starlette.websockets import WebSocketState

from app.core.clock import utcnow
from app.services.session_timer import TimerScheduler
from app.ws.connection_manager import Connection, ConnectionManager
from app.ws.protocol import (
    CloseCode,
    ErrorCode,
    ErrorFrame,
    Hello,
    HostsUpdate,
    Ping,
    RequestCreated,
    RequestResult,
    SessionConnected,
    SessionEnd,
    SessionEndpoint,
    WsError,
    parse_client_message,
)


class FakeWebSocket:
    """Just enough of starlette's WebSocket for the registry."""

    def __init__(self, *, fail: bool = False) -> None:
        self.sent: list[str] = []
        self.close_code: int | None = None
        self.application_state = WebSocketState.CONNECTED
        self.client_state = WebSocketState.CONNECTED
        self._fail = fail

    async def send_text(self, text: str) -> None:
        if self._fail:
            raise RuntimeError("peer is gone")
        self.sent.append(text)

    async def close(self, code: int = 1000) -> None:
        self.close_code = code
        self.application_state = WebSocketState.DISCONNECTED
        self.client_state = WebSocketState.DISCONNECTED


def _connection(fail: bool = False, device_id: uuid.UUID | None = None) -> Connection:
    return Connection(
        FakeWebSocket(fail=fail),  # type: ignore[arg-type]
        device_id=device_id or uuid.uuid4(),
        user_id=uuid.uuid4(),
        public_ip="198.51.100.1",
    )


# ---------------------------------------------------------------- protocol


def test_server_frames_use_the_documented_field_names() -> None:
    assert Ping().to_json() == '{"type":"ping"}'
    assert HostsUpdate(hosts=[]).to_json() == '{"type":"hosts.update","hosts":[]}'

    request_id = uuid.uuid4()
    accepted = RequestResult(request_id=request_id, accepted=True, session_id=request_id)
    assert set(accepted.model_dump()) == {"type", "request_id", "accepted", "reason", "session_id"}

    created = RequestCreated(ref="c1", request_id=request_id, expires_at=utcnow())
    assert '"expires_at":"' in created.to_json()
    assert created.to_json().split('"expires_at":"')[1].startswith("20")
    assert 'Z"}' in created.to_json(), "times are ISO-8601 UTC with a Z suffix"


def test_naive_datetimes_are_serialised_as_utc() -> None:
    naive = (utcnow() + timedelta(minutes=5)).replace(tzinfo=None)
    payload = RequestCreated(ref="c1", request_id=uuid.uuid4(), expires_at=naive).to_json()
    assert payload.count("Z") == 1


def test_error_frame_carries_the_ref() -> None:
    frame = WsError(ErrorCode.HOST_UNAVAILABLE, "nope", ref="c1").to_message()
    assert frame.model_dump() == {
        "type": "error",
        "ref": "c1",
        "code": ErrorCode.HOST_UNAVAILABLE,
        "message": "nope",
    }
    assert ErrorFrame(code=ErrorCode.INTERNAL, message="x").ref is None


def test_parse_client_message_discriminates_on_type() -> None:
    device_id = uuid.uuid4()
    message = parse_client_message(
        f'{{"type":"hello","token":"t","device_id":"{device_id}","app_version":"1.0"}}'
    )
    assert isinstance(message, Hello)
    assert message.device_id == device_id


@pytest.mark.parametrize(
    "raw",
    [
        "not json",
        "[]",
        '{"no_type": 1}',
        '{"type":"nope"}',
        '{"type":"request.create","ref":"c1"}',
        '{"type":"host.available","available":true,"listen_port":70000}',
    ],
)
def test_invalid_frames_raise_bad_request(raw: str) -> None:
    with pytest.raises(WsError) as excinfo:
        parse_client_message(raw)
    assert excinfo.value.code == ErrorCode.BAD_REQUEST


# ---------------------------------------------------------------- session frames (section 3)

SESSION_ID = "5f9d88c4-4d9e-4f0e-9f3f-2c2b0b9a0001"
FINGERPRINT = "9f" * 32


def _endpoint_frame(**overrides: Any) -> str:
    payload: dict[str, Any] = {
        "type": "session.endpoint",
        "session_id": SESSION_ID,
        "cert_fp_sha256": FINGERPRINT,
        "candidates": [{"type": "lan", "ip": "10.0.0.4", "port": 40000}],
    }
    return json.dumps(payload | overrides)


def _many(count: int) -> list[dict[str, Any]]:
    return [{"type": "lan", "ip": f"10.0.0.{n}", "port": 40000} for n in range(count)]


def test_session_frames_parse_into_their_models() -> None:
    endpoint = parse_client_message(_endpoint_frame(candidates=_many(16)))
    assert isinstance(endpoint, SessionEndpoint)
    assert len(endpoint.candidates) == 16
    assert endpoint.candidates[0].identity == ("lan", "10.0.0.0", 40000)

    end = parse_client_message(
        json.dumps(
            {
                "type": "session.end",
                "session_id": SESSION_ID,
                "reason": "host_ended",
                "bytes_up": 10,
                "bytes_down": 20,
            }
        )
    )
    assert isinstance(end, SessionEnd)
    assert end.domains == [], "domains may be omitted; an end must never be refused over it"


def test_candidate_ip_is_canonicalised() -> None:
    frame = parse_client_message(
        _endpoint_frame(candidates=[{"type": "v6", "ip": "2001:0DB8::0001", "port": 443}])
    )
    assert isinstance(frame, SessionEndpoint)
    assert frame.candidates[0].ip == "2001:db8::1"


@pytest.mark.parametrize(
    "raw",
    [
        # cert_fp_sha256: exactly 64 lowercase hex characters
        _endpoint_frame(cert_fp_sha256=FINGERPRINT.upper()),
        _endpoint_frame(cert_fp_sha256="9f" * 31),
        _endpoint_frame(cert_fp_sha256="zz" * 32),
        # candidates: known type, real address, valid port, at most 16, no duplicates
        _endpoint_frame(candidates=[{"type": "relay", "ip": "10.0.0.4", "port": 1}]),
        _endpoint_frame(candidates=[{"type": "lan", "ip": "not-an-ip", "port": 1}]),
        _endpoint_frame(candidates=[{"type": "lan", "ip": "example.com", "port": 1}]),
        _endpoint_frame(candidates=[{"type": "lan", "ip": "10.0.0.4", "port": 0}]),
        _endpoint_frame(candidates=[{"type": "lan", "ip": "10.0.0.4", "port": 65536}]),
        _endpoint_frame(candidates=_many(17)),
        _endpoint_frame(candidates=_many(1) + _many(1)),
        _endpoint_frame(candidates="nope"),
        # the whole frame
        '{"type":"session.endpoint","session_id":"nope","cert_fp_sha256":"' + FINGERPRINT + '"}',
        # session.connected
        '{"type":"session.connected","session_id":"' + SESSION_ID + '","winner_type":"carrier",'
        '"connect_ms":1,"tls_version":"1.3"}',
        '{"type":"session.connected","session_id":"' + SESSION_ID + '","winner_type":"lan",'
        '"connect_ms":-1,"tls_version":"1.3"}',
        '{"type":"session.connected","session_id":"' + SESSION_ID + '","winner_type":"lan",'
        '"connect_ms":1,"tls_version":"1.1"}',
        # session.stats / session.end
        '{"type":"session.stats","session_id":"' + SESSION_ID + '","bytes_up":-1,"bytes_down":0}',
        '{"type":"session.stats","session_id":"' + SESSION_ID + '","bytes_up":0}',
        '{"type":"session.end","session_id":"' + SESSION_ID + '","reason":"expired",'
        '"bytes_up":0,"bytes_down":0}',
        '{"type":"session.end","session_id":"' + SESSION_ID + '","reason":"guest_ended",'
        '"bytes_up":0,"bytes_down":-3}',
        # session.connect_failed
        '{"type":"session.connect_failed","session_id":"' + SESSION_ID + '"}',
        '{"type":"session.connect_failed","session_id":"' + SESSION_ID + '","diagnostics":[]}',
    ],
)
def test_invalid_session_frames_raise_bad_request(raw: str) -> None:
    with pytest.raises(WsError) as excinfo:
        parse_client_message(raw)
    assert excinfo.value.code == ErrorCode.BAD_REQUEST


def test_parse_errors_never_echo_the_token() -> None:
    secret = "eyJhbGciOiJIUzI1NiJ9.super-secret-token"
    with pytest.raises(WsError) as excinfo:
        parse_client_message(f'{{"type":"hello","token":"{secret}","device_id":"not-a-uuid"}}')
    assert secret not in excinfo.value.message


def test_a_ref_is_echoed_even_when_validation_fails() -> None:
    with pytest.raises(WsError) as excinfo:
        parse_client_message('{"type":"request.cancel","ref":"c7","request_id":"nope"}')
    assert excinfo.value.ref == "c7"


# ---------------------------------------------------------------- connection manager


async def test_register_closes_the_previous_connection_with_4409() -> None:
    manager = ConnectionManager()
    device_id = uuid.uuid4()
    first = _connection(device_id=device_id)
    second = _connection(device_id=device_id)

    assert await manager.register(first) is None
    assert await manager.register(second) is first
    assert first.closed.is_set()
    assert first.websocket.close_code == CloseCode.SUPERSEDED  # type: ignore[attr-defined]
    assert manager.is_connected(device_id)
    assert len(manager) == 1


async def test_a_superseded_connection_cannot_unregister_the_newer_one() -> None:
    manager = ConnectionManager()
    device_id = uuid.uuid4()
    first, second = _connection(device_id=device_id), _connection(device_id=device_id)
    await manager.register(first)
    await manager.register(second)

    assert await manager.unregister(first) is False
    assert manager.get(device_id) is second
    assert await manager.unregister(second) is True
    assert manager.get(device_id) is None


async def test_send_to_a_dead_peer_marks_the_connection_closed() -> None:
    manager = ConnectionManager()
    broken = _connection(fail=True)
    await manager.register(broken)

    assert await manager.send(broken.device_id, Ping()) is False
    assert broken.closed.is_set()
    assert await manager.send(uuid.uuid4(), Ping()) is False


async def test_broadcast_honours_the_predicate() -> None:
    manager = ConnectionManager()
    a, b = _connection(), _connection()
    await manager.register(a)
    await manager.register(b)

    assert await manager.broadcast(Ping(), lambda c: c is a) == 1
    assert len(a.websocket.sent) == 1  # type: ignore[attr-defined]
    assert len(b.websocket.sent) == 0  # type: ignore[attr-defined]
    assert await manager.broadcast(Ping()) == 2


async def test_close_all_uses_1012_and_is_idempotent() -> None:
    manager = ConnectionManager()
    connection = _connection()
    await manager.register(connection)

    assert await manager.close_all() == 1
    assert connection.websocket.close_code == CloseCode.SERVER_RESTART  # type: ignore[attr-defined]
    await connection.close(CloseCode.NORMAL)
    assert connection.close_code == CloseCode.SERVER_RESTART


# ---------------------------------------------------------------- scheduler


async def test_scheduler_fires_and_forgets() -> None:
    scheduler = TimerScheduler()
    fired: list[str] = []

    async def callback() -> None:
        fired.append("once")

    scheduler.schedule_after("k", 0.01, callback)
    assert scheduler.is_scheduled("k")
    await asyncio.sleep(0.05)
    assert fired == ["once"]
    assert len(scheduler) == 0


async def test_rescheduling_a_key_replaces_the_timer() -> None:
    scheduler = TimerScheduler()
    fired: list[str] = []

    def make(tag: str) -> Any:
        async def callback() -> None:
            fired.append(tag)

        return callback

    scheduler.schedule_after("k", 0.01, make("first"))
    scheduler.schedule_after("k", 0.02, make("second"))
    await asyncio.sleep(0.06)
    assert fired == ["second"]


async def test_cancel_and_cancel_all() -> None:
    scheduler = TimerScheduler()
    fired: list[str] = []

    async def callback() -> None:  # pragma: no cover - must not run
        fired.append("x")

    scheduler.schedule_after("a", 0.05, callback)
    scheduler.schedule_at("b", utcnow() + timedelta(seconds=0.05), callback)
    assert scheduler.cancel("a") is True
    assert scheduler.cancel("a") is False
    assert scheduler.cancel_all() == 1
    await asyncio.sleep(0.1)
    assert fired == []


async def test_a_failing_callback_is_logged_not_raised(
    caplog: pytest.LogCaptureFixture,
) -> None:
    scheduler = TimerScheduler()

    async def boom() -> None:
        raise RuntimeError("boom")

    with caplog.at_level("ERROR", logger="app.services.session_timer"):
        scheduler.schedule_after("k", 0.0, boom)
        await asyncio.sleep(0.05)
    assert any("scheduled timer failed" in record.message for record in caplog.records)


async def test_a_past_deadline_fires_immediately() -> None:
    scheduler = TimerScheduler()
    fired = asyncio.Event()

    async def callback() -> None:
        fired.set()

    scheduler.schedule_at("k", utcnow() - timedelta(seconds=10), callback)
    await asyncio.wait_for(fired.wait(), 1.0)


def test_relay_wins_a_session_but_is_never_a_candidate() -> None:
    """ADR-0009 widened ``winner_type`` without widening ``candidates``.

    The two vocabularies look alike and are not the same one. ``session.endpoint`` lists addresses
    the peer can dial, and the relay is never one of those: a client reaches it through
    ``session.created.relay``, not by dialling a candidate. But ``relay`` is a truthful answer to
    "what carried this session", and the admin diagnostics that feed the transport decision are
    worthless if it cannot be reported."""
    frame = parse_client_message(
        '{"type":"session.connected","session_id":"' + SESSION_ID + '","winner_type":"relay",'
        '"connect_ms":42,"tls_version":"1.3"}'
    )
    assert isinstance(frame, SessionConnected)
    assert frame.winner_type == "relay"

    with pytest.raises(WsError):
        parse_client_message(
            _endpoint_frame(candidates=[{"type": "relay", "ip": "10.0.0.4", "port": 443}])
        )
