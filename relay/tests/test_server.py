"""End-to-end over real sockets: every reply the client can receive, and the byte pump itself.

The client half these exercise is Josour.Tunnel.Transport.RelayTransport; its own tests run
against an in-process FakeRelay. These run the real service against a hand-built client, so a
disagreement between the two halves shows up here rather than on a live session.
"""

from __future__ import annotations

import asyncio
import uuid

import pytest

from relay.config import Settings
from relay.protocol import REPLY_LENGTH, VERSION, Role, Status, build_preamble
from relay.server import Relay
from relay.tokens import mint

SECRET = "relay-secret-that-is-at-least-32-bytes-long"


def settings(**overrides) -> Settings:
    base = {
        "relay_secret": SECRET,
        "relay_host": "127.0.0.1",
        "relay_port": 0,
        "preamble_timeout_seconds": 0.5,
        "pair_timeout_seconds": 0.5,
        "idle_timeout_seconds": 2.0,
        "max_session_seconds": 30.0,
    }
    return Settings(**{**base, **overrides})


@pytest.fixture
async def relay():
    instance = Relay(settings())
    await instance.start()
    yield instance
    await instance.close()


async def dial(relay: Relay) -> tuple[asyncio.StreamReader, asyncio.StreamWriter]:
    return await asyncio.open_connection("127.0.0.1", relay.port)


async def hello(
    relay: Relay,
    session_id: uuid.UUID,
    role: Role,
    *,
    token: str | None = None,
) -> tuple[asyncio.StreamReader, asyncio.StreamWriter]:
    reader, writer = await dial(relay)
    writer.write(build_preamble(session_id, role, token or mint(SECRET, session_id, role)))
    await writer.drain()
    return reader, writer


async def read_status(reader: asyncio.StreamReader) -> Status:
    reply = await asyncio.wait_for(reader.readexactly(REPLY_LENGTH), 5)
    assert reply[0] == VERSION
    return Status(reply[1])


async def close(*writers: asyncio.StreamWriter) -> None:
    for writer in writers:
        writer.close()
    await asyncio.gather(*(w.wait_closed() for w in writers), return_exceptions=True)


# ---------------------------------------------------------------- pairing


async def test_two_peers_pair_and_bytes_flow_both_ways(relay: Relay) -> None:
    session = uuid.uuid4()
    guest_r, guest_w = await hello(relay, session, Role.GUEST)
    host_r, host_w = await hello(relay, session, Role.HOST)

    assert await read_status(guest_r) is Status.PAIRED
    assert await read_status(host_r) is Status.PAIRED

    guest_w.write(b"AUTH1-ish")
    await guest_w.drain()
    assert await asyncio.wait_for(host_r.readexactly(9), 5) == b"AUTH1-ish"

    host_w.write(b"AUTH2")
    await host_w.drain()
    assert await asyncio.wait_for(guest_r.readexactly(5), 5) == b"AUTH2"

    assert relay.counters.paired == 1
    await close(guest_w, host_w)


async def test_pairing_does_not_depend_on_arrival_order(relay: Relay) -> None:
    session = uuid.uuid4()
    host_r, host_w = await hello(relay, session, Role.HOST)
    guest_r, guest_w = await hello(relay, session, Role.GUEST)
    assert await read_status(host_r) is Status.PAIRED
    assert await read_status(guest_r) is Status.PAIRED
    await close(guest_w, host_w)


async def test_sessions_do_not_cross(relay: Relay) -> None:
    """Two live sessions at once, each seeing only its own bytes."""
    one, two = uuid.uuid4(), uuid.uuid4()
    a_r, a_w = await hello(relay, one, Role.GUEST)
    b_r, b_w = await hello(relay, one, Role.HOST)
    c_r, c_w = await hello(relay, two, Role.GUEST)
    d_r, d_w = await hello(relay, two, Role.HOST)
    for reader in (a_r, b_r, c_r, d_r):
        assert await read_status(reader) is Status.PAIRED

    a_w.write(b"one")
    c_w.write(b"two")
    await asyncio.gather(a_w.drain(), c_w.drain())
    assert await asyncio.wait_for(b_r.readexactly(3), 5) == b"one"
    assert await asyncio.wait_for(d_r.readexactly(3), 5) == b"two"
    await close(a_w, b_w, c_w, d_w)


async def test_large_payload_survives_the_pump(relay: Relay) -> None:
    session = uuid.uuid4()
    guest_r, guest_w = await hello(relay, session, Role.GUEST)
    host_r, host_w = await hello(relay, session, Role.HOST)
    await read_status(guest_r)
    await read_status(host_r)

    payload = bytes(range(256)) * 4096  # 1 MiB, several read chunks
    guest_w.write(payload)
    await guest_w.drain()
    assert await asyncio.wait_for(host_r.readexactly(len(payload)), 20) == payload
    await close(guest_w, host_w)


async def test_half_close_is_propagated_not_torn_down(relay: Relay) -> None:
    """One side finishing must not cut off a reply still in flight from the other."""
    session = uuid.uuid4()
    guest_r, guest_w = await hello(relay, session, Role.GUEST)
    host_r, host_w = await hello(relay, session, Role.HOST)
    await read_status(guest_r)
    await read_status(host_r)

    guest_w.write(b"bye")
    await guest_w.drain()
    guest_w.write_eof()

    assert await asyncio.wait_for(host_r.readexactly(3), 5) == b"bye"
    assert await asyncio.wait_for(host_r.read(1), 5) == b""  # EOF reached the host

    host_w.write(b"late reply")
    await host_w.drain()
    assert await asyncio.wait_for(guest_r.readexactly(10), 5) == b"late reply"
    await close(guest_w, host_w)


# ---------------------------------------------------------------- rejections


async def test_same_role_twice_is_busy_and_the_first_keeps_the_slot(relay: Relay) -> None:
    session = uuid.uuid4()
    first_r, first_w = await hello(relay, session, Role.GUEST)
    second_r, second_w = await hello(relay, session, Role.GUEST)
    assert await read_status(second_r) is Status.BUSY

    host_r, host_w = await hello(relay, session, Role.HOST)
    assert await read_status(first_r) is Status.PAIRED
    assert await read_status(host_r) is Status.PAIRED
    await close(first_w, second_w, host_w)


async def test_lone_peer_is_told_no_peer(relay: Relay) -> None:
    reader, writer = await hello(relay, uuid.uuid4(), Role.HOST)
    assert await read_status(reader) is Status.NO_PEER
    assert relay.counters.rejected_no_peer == 1
    await close(writer)


async def test_slot_is_released_after_no_peer(relay: Relay) -> None:
    """A timed-out waiter must not keep occupying its session id."""
    session = uuid.uuid4()
    first_r, first_w = await hello(relay, session, Role.HOST)
    assert await read_status(first_r) is Status.NO_PEER

    retry_r, retry_w = await hello(relay, session, Role.HOST)
    guest_r, guest_w = await hello(relay, session, Role.GUEST)
    assert await read_status(retry_r) is Status.PAIRED
    assert await read_status(guest_r) is Status.PAIRED
    await close(first_w, retry_w, guest_w)


async def test_token_signed_by_someone_else_is_unauthorized(relay: Relay) -> None:
    session = uuid.uuid4()
    forged = mint("another-secret-that-is-at-least-32-bytes", session, Role.HOST)
    reader, writer = await hello(relay, session, Role.HOST, token=forged)
    assert await read_status(reader) is Status.UNAUTHORIZED
    await close(writer)


async def test_token_for_another_session_is_unauthorized(relay: Relay) -> None:
    reader, writer = await hello(
        relay, uuid.uuid4(), Role.HOST, token=mint(SECRET, uuid.uuid4(), Role.HOST)
    )
    assert await read_status(reader) is Status.UNAUTHORIZED
    await close(writer)


async def test_a_guest_token_cannot_open_the_host_side(relay: Relay) -> None:
    session = uuid.uuid4()
    reader, writer = await hello(relay, session, Role.HOST, token=mint(SECRET, session, Role.GUEST))
    assert await read_status(reader) is Status.UNAUTHORIZED
    await close(writer)


async def test_garbage_preamble_is_a_protocol_error(relay: Relay) -> None:
    reader, writer = await dial(relay)
    writer.write(b"GET / HTTP/1.1\r\nHost: x\r\n\r\n")
    await writer.drain()
    assert await read_status(reader) is Status.PROTOCOL_ERROR
    await close(writer)


async def test_silence_times_out_without_holding_the_slot(relay: Relay) -> None:
    """A scanner that connects and says nothing gets the preamble deadline, not a session slot."""
    reader, writer = await dial(relay)
    assert await read_status(reader) is Status.PROTOCOL_ERROR
    await close(writer)


async def test_truncated_preamble_is_a_protocol_error(relay: Relay) -> None:
    reader, writer = await dial(relay)
    writer.write(build_preamble(uuid.uuid4(), Role.HOST, "tok")[:10])
    await writer.drain()
    assert await read_status(reader) is Status.PROTOCOL_ERROR
    await close(writer)


# ---------------------------------------------------------------- limits


async def test_session_table_is_capped() -> None:
    relay = Relay(settings(max_sessions=1, pair_timeout_seconds=5.0))
    await relay.start()
    try:
        first_r, first_w = await hello(relay, uuid.uuid4(), Role.HOST)
        second_r, second_w = await hello(relay, uuid.uuid4(), Role.HOST)
        assert await read_status(second_r) is Status.INTERNAL
        assert relay.counters.rejected_over_capacity == 1
        await close(first_w, second_w)
    finally:
        await relay.close()


async def test_connections_per_ip_are_capped() -> None:
    relay = Relay(settings(max_connections_per_ip=2, pair_timeout_seconds=5.0))
    await relay.start()
    try:
        _, a = await hello(relay, uuid.uuid4(), Role.HOST)
        _, b = await hello(relay, uuid.uuid4(), Role.HOST)
        third_r, c = await dial(relay)
        assert await read_status(third_r) is Status.INTERNAL
        await close(a, b, c)
    finally:
        await relay.close()


async def test_idle_session_is_dropped() -> None:
    relay = Relay(settings(idle_timeout_seconds=1.0))
    await relay.start()
    try:
        session = uuid.uuid4()
        guest_r, guest_w = await hello(relay, session, Role.GUEST)
        host_r, host_w = await hello(relay, session, Role.HOST)
        await read_status(guest_r)
        await read_status(host_r)
        assert await asyncio.wait_for(guest_r.read(1), 10) == b""
        await close(guest_w, host_w)
    finally:
        await relay.close()


async def test_byte_cap_ends_the_session() -> None:
    relay = Relay(settings(max_bytes_per_session=1024, idle_timeout_seconds=30.0))
    await relay.start()
    try:
        session = uuid.uuid4()
        guest_r, guest_w = await hello(relay, session, Role.GUEST)
        host_r, host_w = await hello(relay, session, Role.HOST)
        await read_status(guest_r)
        await read_status(host_r)
        guest_w.write(b"x" * 4096)
        await guest_w.drain()
        assert await asyncio.wait_for(host_r.read(4096), 10)
        assert await asyncio.wait_for(host_r.read(1), 10) == b""
        await close(guest_w, host_w)
    finally:
        await relay.close()


async def test_counters_report_what_happened(relay: Relay) -> None:
    session = uuid.uuid4()
    guest_r, guest_w = await hello(relay, session, Role.GUEST)
    host_r, host_w = await hello(relay, session, Role.HOST)
    await read_status(guest_r)
    await read_status(host_r)
    guest_w.write(b"hello")
    await guest_w.drain()
    await asyncio.wait_for(host_r.readexactly(5), 5)
    await close(guest_w, host_w)
    await asyncio.sleep(0.2)

    assert relay.counters.accepted == 2
    assert relay.counters.paired == 1
    assert relay.counters.bytes_relayed >= 5
