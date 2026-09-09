"""The relay: pair two peers by session id, then pump opaque bytes between them.

What this service is *not* is the point of its design. It does not terminate TLS, hold session
state, read a database, or understand a single byte it forwards - the peers' own TLS handshake,
certificate pinning and AUTH1/AUTH2 all run end to end inside this stream (docs/protocol.md
sections 3 and 4). A relay operator, or anyone who takes the relay, sees ciphertext and traffic
volumes. That is what lets ADR-0009 change the product claim to "the server cannot read browsing
data" rather than abandoning it.

Lifecycle of one connection:

    accept -> per-IP cap -> read preamble (5 s) -> verify token -> pair -> pump -> close

Every rejection is a two-byte reply and an immediate close, so a scanner learns nothing beyond
"something is listening", and holds a slot for at most ``preamble_timeout_seconds``.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import time
import uuid
from dataclasses import dataclass, field

from relay.config import Settings
from relay.protocol import (
    FIXED_PREAMBLE_LENGTH,
    MAX_TOKEN_LENGTH,
    TOKEN_LENGTH_OFFSET,
    Preamble,
    ProtocolError,
    Status,
    build_reply,
    parse_preamble,
)
from relay.tokens import TokenError, verify

log = logging.getLogger("relay")

CHUNK = 64 * 1024


@dataclass(slots=True)
class Counters:
    """Process-wide totals, for the operator and for the capacity question ADR-0009 leaves open."""

    accepted: int = 0
    rejected_protocol: int = 0
    rejected_unauthorized: int = 0
    rejected_busy: int = 0
    rejected_no_peer: int = 0
    rejected_over_capacity: int = 0
    paired: int = 0
    bytes_relayed: int = 0


@dataclass(slots=True)
class _Waiting:
    """A party that arrived first and is holding a slot until its peer shows up."""

    preamble: Preamble
    reader: asyncio.StreamReader
    writer: asyncio.StreamWriter
    peer: asyncio.Future = field(default_factory=lambda: asyncio.get_running_loop().create_future())
    finished: asyncio.Future = field(
        default_factory=lambda: asyncio.get_running_loop().create_future()
    )


class Relay:
    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._waiting: dict[uuid.UUID, _Waiting] = {}
        self._per_ip: dict[str, int] = {}
        self._lock = asyncio.Lock()
        self._server: asyncio.AbstractServer | None = None
        self.counters = Counters()

    # ------------------------------------------------------------------ lifecycle

    async def start(self) -> asyncio.AbstractServer:
        self._server = await asyncio.start_server(
            self._handle, self._settings.relay_host, self._settings.relay_port
        )
        bound = ", ".join(str(s.getsockname()) for s in self._server.sockets or ())
        log.info("relay listening on %s", bound)
        return self._server

    @property
    def port(self) -> int:
        """The bound port; meaningful after :meth:`start`, and the way tests use port 0."""
        assert self._server is not None and self._server.sockets
        return self._server.sockets[0].getsockname()[1]

    async def close(self) -> None:
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()

    # ------------------------------------------------------------------ connection

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        peer_ip = _peer_ip(writer)
        self.counters.accepted += 1
        if not await self._claim_ip(peer_ip):
            self.counters.rejected_over_capacity += 1
            log.warning("connection cap reached for %s", peer_ip)
            await _reply_and_close(writer, Status.INTERNAL)
            return
        try:
            await self._serve(reader, writer, peer_ip)
        except Exception:  # pragma: no cover - a handler must never take the server down
            log.exception("relay handler failed")
            await _reply_and_close(writer, Status.INTERNAL)
        finally:
            await self._release_ip(peer_ip)

    async def _serve(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter, peer_ip: str
    ) -> None:
        try:
            preamble = await asyncio.wait_for(
                _read_preamble(reader), self._settings.preamble_timeout_seconds
            )
        except (ProtocolError, asyncio.IncompleteReadError):
            self.counters.rejected_protocol += 1
            log.info("bad preamble from %s", peer_ip)
            await _reply_and_close(writer, Status.PROTOCOL_ERROR)
            return
        except TimeoutError:
            self.counters.rejected_protocol += 1
            log.info("preamble timed out from %s", peer_ip)
            await _reply_and_close(writer, Status.PROTOCOL_ERROR)
            return

        try:
            verify(self._settings.relay_secret, preamble.token, preamble.session_id, preamble.role)
        except TokenError as exc:
            self.counters.rejected_unauthorized += 1
            # The session id is safe to log and is what ties this line to a control-plane row;
            # the token never is.
            log.warning(
                "unauthorized %s for session %s from %s: %s",
                preamble.role,
                preamble.session_id,
                peer_ip,
                exc,
            )
            await _reply_and_close(writer, Status.UNAUTHORIZED)
            return

        await self._pair(preamble, reader, writer, peer_ip)

    # ------------------------------------------------------------------ pairing

    async def _pair(
        self,
        preamble: Preamble,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
        peer_ip: str,
    ) -> None:
        async with self._lock:
            waiting = self._waiting.get(preamble.session_id)
            if waiting is None:
                if len(self._waiting) >= self._settings.max_sessions:
                    self.counters.rejected_over_capacity += 1
                    log.warning("session table full; refusing %s", preamble.session_id)
                    await _reply_and_close(writer, Status.INTERNAL)
                    return
                me = _Waiting(preamble, reader, writer)
                self._waiting[preamble.session_id] = me
                first = True
            elif waiting.preamble.role is preamble.role:
                # The role is bound into the token, so this is not a stranger taking a slot: it is
                # the same party dialling twice. The original keeps the slot.
                self.counters.rejected_busy += 1
                log.info("duplicate %s for session %s", preamble.role, preamble.session_id)
                await _reply_and_close(writer, Status.BUSY)
                return
            else:
                del self._waiting[preamble.session_id]
                me = _Waiting(preamble, reader, writer)
                waiting.peer.set_result(me)
                first = False

        if first:
            await self._wait_for_peer(me, peer_ip)
            return

        await self._run_pair(waiting, me)

    async def _wait_for_peer(self, me: _Waiting, peer_ip: str) -> None:
        try:
            await asyncio.wait_for(asyncio.shield(me.peer), self._settings.pair_timeout_seconds)
        except TimeoutError:
            # The peer may have arrived in the instant this fired. Both the hand-off and the
            # removal happen under the lock, so re-checking here settles the race.
            async with self._lock:
                if not me.peer.done():
                    if self._waiting.get(me.preamble.session_id) is me:
                        del self._waiting[me.preamble.session_id]
                    self.counters.rejected_no_peer += 1
                    log.info(
                        "no peer for session %s (%s waited %.0fs)",
                        me.preamble.session_id,
                        me.preamble.role,
                        self._settings.pair_timeout_seconds,
                    )
                    await _reply_and_close(me.writer, Status.NO_PEER)
                    return
        # Paired: the second arrival owns the pump; stay alive until it says the session is over.
        await me.finished

    async def _run_pair(self, first: _Waiting, second: _Waiting) -> None:
        session_id = second.preamble.session_id
        self.counters.paired += 1
        started = time.monotonic()
        await asyncio.gather(
            _reply(first.writer, Status.PAIRED), _reply(second.writer, Status.PAIRED)
        )
        log.info("paired session %s", session_id)

        activity = _Activity()
        totals = [0, 0]
        reason = "closed"
        try:
            reason = await self._relay_bytes(first, second, activity, totals)
        except Exception:  # pragma: no cover - both sides are closed below regardless
            log.exception("relay pump failed for session %s", session_id)
            reason = "error"
        finally:
            self.counters.bytes_relayed += totals[0] + totals[1]
            await asyncio.gather(
                _close(first.writer), _close(second.writer), return_exceptions=True
            )
            if not second.finished.done():
                second.finished.set_result(None)
            if not first.finished.done():
                first.finished.set_result(None)
            log.info(
                "session %s ended (%s) after %.1fs: %d B host-ward, %d B guest-ward",
                session_id,
                reason,
                time.monotonic() - started,
                totals[0],
                totals[1],
            )

    async def _relay_bytes(
        self, a: _Waiting, b: _Waiting, activity: _Activity, totals: list[int]
    ) -> str:
        cap = self._settings.max_bytes_per_session
        pumps = [
            asyncio.create_task(_pump(a.reader, b.writer, activity, totals, 0)),
            asyncio.create_task(_pump(b.reader, a.writer, activity, totals, 1)),
        ]
        watchdog = asyncio.create_task(self._watch(activity, totals, cap))
        try:
            done, _ = await asyncio.wait([*pumps, watchdog], return_when=asyncio.FIRST_COMPLETED)
            if watchdog in done:
                return watchdog.result()
            # One direction reached EOF. Give the other a moment to drain its own EOF rather
            # than cutting a reply short, but never wait on it indefinitely.
            with contextlib.suppress(TimeoutError):
                await asyncio.wait_for(asyncio.gather(*pumps, return_exceptions=True), 5.0)
            return "closed"
        finally:
            for task in (*pumps, watchdog):
                task.cancel()
            await asyncio.gather(*pumps, watchdog, return_exceptions=True)

    async def _watch(self, activity: _Activity, totals: list[int], cap: int) -> str:
        """Ends a session that has gone quiet, overrun its ceiling, or outlived the hard cap."""
        started = time.monotonic()
        while True:
            await asyncio.sleep(1.0)
            now = time.monotonic()
            if now - activity.last > self._settings.idle_timeout_seconds:
                return "idle"
            if now - started > self._settings.max_session_seconds:
                return "max_duration"
            if cap and totals[0] + totals[1] > cap:
                return "byte_cap"

    # ------------------------------------------------------------------ per-IP cap

    async def _claim_ip(self, peer_ip: str) -> bool:
        async with self._lock:
            current = self._per_ip.get(peer_ip, 0)
            if current >= self._settings.max_connections_per_ip:
                return False
            self._per_ip[peer_ip] = current + 1
            return True

    async def _release_ip(self, peer_ip: str) -> None:
        async with self._lock:
            remaining = self._per_ip.get(peer_ip, 1) - 1
            if remaining <= 0:
                self._per_ip.pop(peer_ip, None)
            else:
                self._per_ip[peer_ip] = remaining


# ---------------------------------------------------------------------- helpers


class _Activity:
    __slots__ = ("last",)

    def __init__(self) -> None:
        self.last = time.monotonic()

    def touch(self) -> None:
        self.last = time.monotonic()


async def _pump(
    reader: asyncio.StreamReader,
    writer: asyncio.StreamWriter,
    activity: _Activity,
    totals: list[int],
    slot: int,
) -> None:
    while True:
        data = await reader.read(CHUNK)
        if not data:
            break
        writer.write(data)
        await writer.drain()
        totals[slot] += len(data)
        activity.touch()
    # Propagate the half-close instead of tearing the whole connection down: the peer may still
    # have a reply in flight, exactly as the tunnel's own CLOSE is half-duplex.
    with contextlib.suppress(OSError, RuntimeError):
        if writer.can_write_eof():
            writer.write_eof()


async def _read_preamble(reader: asyncio.StreamReader) -> Preamble:
    header = await reader.readexactly(FIXED_PREAMBLE_LENGTH)
    token_length = int.from_bytes(header[TOKEN_LENGTH_OFFSET : TOKEN_LENGTH_OFFSET + 2], "big")
    if token_length == 0 or token_length > MAX_TOKEN_LENGTH:
        raise ProtocolError("bad token length")
    return parse_preamble(header + await reader.readexactly(token_length))


def _peer_ip(writer: asyncio.StreamWriter) -> str:
    peer = writer.get_extra_info("peername")
    return peer[0] if peer else "unknown"


async def _reply(writer: asyncio.StreamWriter, status: Status) -> None:
    with contextlib.suppress(OSError, RuntimeError, ConnectionError):
        writer.write(build_reply(status))
        await writer.drain()


async def _close(writer: asyncio.StreamWriter) -> None:
    with contextlib.suppress(OSError, RuntimeError, ConnectionError):
        writer.close()
        await writer.wait_closed()


async def _reply_and_close(writer: asyncio.StreamWriter, status: Status) -> None:
    await _reply(writer, status)
    await _close(writer)
