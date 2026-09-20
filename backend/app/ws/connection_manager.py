"""In-memory registry of live ``/ws`` connections, one per ``device_id``.

The deployment model is a single uvicorn worker (see the Dockerfile), so the registry is a plain
dict guarded by asyncio's single-threaded semantics: the critical sections below contain no
``await``, which makes read-modify-write on the dict atomic without a lock.

A second connection for the same device wins: the previous one is closed with ``4409``
(docs/ws-protocol.md section 1). Cleanup of the superseded connection is identity-checked in
:meth:`ConnectionManager.unregister`, so the newcomer's presence row is never torn down by the
connection it replaced.
"""

import asyncio
import logging
import time
import uuid
from collections.abc import Callable, Iterator

from starlette.websockets import WebSocket, WebSocketDisconnect, WebSocketState

from app.core.rate_limit import TokenBucketLimiter
from app.ws.protocol import CloseCode, ServerMessage

log = logging.getLogger(__name__)

# Errors raised when the peer is already gone; sending must never break the caller.
_SEND_ERRORS = (WebSocketDisconnect, RuntimeError, OSError)

FRAME_BUDGET = 100
FRAME_BUDGET_SECONDS = 10.0
"""Per-connection frame allowance (product document section 14, "Rate limiting").

Authentication is not a licence to spin the single worker's event loop: every frame after
``hello`` costs a parse and usually a database round trip, so an authenticated device could
otherwise stall every other session on the box. The budget is deliberately far above any real
client - one ``pong`` per 20 s, ``session.stats`` every 30 s and a handful of frames per session
is well under ten per window - and a client that exceeds it gets ``error(rate_limited)``
(docs/ws-protocol.md section 2) for the offending frame, not a disconnect."""


class Connection:
    """One authenticated WebSocket. ``send`` is serialised per connection so concurrent
    producers (heartbeat, broadcasts, request flows) cannot interleave frames."""

    def __init__(
        self,
        websocket: WebSocket,
        *,
        device_id: uuid.UUID,
        user_id: uuid.UUID,
        public_ip: str | None,
    ) -> None:
        self.websocket = websocket
        self.device_id = device_id
        self.user_id = user_id
        self.public_ip = public_ip
        self.closed = asyncio.Event()
        self.close_code: int | None = None
        self._lock = asyncio.Lock()
        self._frames = TokenBucketLimiter(FRAME_BUDGET, FRAME_BUDGET_SECONDS, max_keys=1)
        self.dropped_frames = 0
        """Frames refused by :meth:`accept_frame` over this connection's whole life. It only
        grows, so ``== 1`` is the first refusal: the one worth a log line. Logging every refused
        frame would turn a frame flood into a log flood."""
        now = time.monotonic()
        self.connected_at = now
        self.last_pong_at = now
        self.last_ping_at = now

    def mark_pong(self) -> None:
        self.last_pong_at = time.monotonic()

    def accept_frame(self) -> bool:
        """Consume one frame from this connection's budget; ``False`` means it is exhausted."""
        if self._frames.check("frames").allowed:
            return True
        self.dropped_frames += 1
        return False

    async def send(self, message: ServerMessage) -> bool:
        """Best-effort send; ``False`` means the peer is gone (the connection is then marked
        closed so the owning task tears it down)."""
        return await self.send_text(message.to_json())

    async def send_text(self, text: str) -> bool:
        """Send an already-rendered frame.

        The fan-out of ``hosts.update`` renders one JSON body and hands the same string to every
        recipient that must see the same list, which is what keeps the broadcast from costing
        one serialisation per connection (docs/load-test-week5.md)."""
        if self.closed.is_set():
            return False
        async with self._lock:
            if self.websocket.application_state is not WebSocketState.CONNECTED:
                return False
            try:
                await self.websocket.send_text(text)
            except _SEND_ERRORS as exc:
                log.debug(
                    "ws send failed", extra={"device_id": str(self.device_id), "error": str(exc)}
                )
                self.closed.set()
                return False
        return True

    async def close(self, code: int = CloseCode.NORMAL) -> None:
        """Idempotent. Sets :attr:`closed` first so the owning task stops its read loop."""
        if self.closed.is_set():
            return
        self.close_code = int(code)
        self.closed.set()
        async with self._lock:
            if WebSocketState.DISCONNECTED in (
                self.websocket.application_state,
                self.websocket.client_state,
            ):
                return
            try:
                await self.websocket.close(code=int(code))
            except _SEND_ERRORS:
                pass


type ConnectionPredicate = Callable[[Connection], bool]


class ConnectionManager:
    def __init__(self) -> None:
        self._connections: dict[uuid.UUID, Connection] = {}

    # -- registry ---------------------------------------------------------------------------

    async def register(self, connection: Connection) -> Connection | None:
        """Install ``connection`` and close any previous one for the same device with 4409.
        Returns the superseded connection, if there was one."""
        previous = self._connections.get(connection.device_id)
        self._connections[connection.device_id] = connection  # no await: atomic swap
        if previous is not None and previous is not connection:
            log.info("ws superseded", extra={"device_id": str(connection.device_id)})
            await previous.close(CloseCode.SUPERSEDED)
        return previous

    async def unregister(self, connection: Connection) -> bool:
        """Remove ``connection`` if it is still the registered one for its device.

        ``False`` means it was already superseded, and the caller must not touch the device's
        presence, requests or sessions - they belong to the newer connection."""
        if self._connections.get(connection.device_id) is not connection:
            return False
        del self._connections[connection.device_id]
        return True

    # -- lookup -----------------------------------------------------------------------------

    def get(self, device_id: uuid.UUID) -> Connection | None:
        return self._connections.get(device_id)

    def is_connected(self, device_id: uuid.UUID) -> bool:
        connection = self._connections.get(device_id)
        return connection is not None and not connection.closed.is_set()

    def connections(self) -> list[Connection]:
        """Snapshot for presence fan-out; safe to iterate while connections come and go."""
        return list(self._connections.values())

    def __iter__(self) -> Iterator[Connection]:
        return iter(self.connections())

    def __len__(self) -> int:
        return len(self._connections)

    # -- fan-out ----------------------------------------------------------------------------

    async def send(self, device_id: uuid.UUID, message: ServerMessage) -> bool:
        connection = self._connections.get(device_id)
        if connection is None:
            return False
        return await connection.send(message)

    async def broadcast(
        self, message: ServerMessage, predicate: ConnectionPredicate | None = None
    ) -> int:
        """Send to every connection (optionally filtered). Returns the delivery count."""
        delivered = 0
        for connection in self.connections():
            if predicate is not None and not predicate(connection):
                continue
            if await connection.send(message):
                delivered += 1
        return delivered

    async def close_all(self, code: int = CloseCode.SERVER_RESTART) -> int:
        """Shutdown path: every client is told to reconnect with backoff (1012)."""
        connections = self.connections()
        for connection in connections:
            await connection.close(code)
        return len(connections)

    def reset(self) -> None:
        """Drop the registry without touching the sockets (tests, and after ``close_all``)."""
        self._connections.clear()


connection_manager = ConnectionManager()
"""Process-wide registry, like ``event_bus`` and ``settings_service``."""
