"""A minimal in-process ASGI WebSocket client.

``httpx`` cannot speak WebSocket and starlette's ``TestClient`` runs the app in a second thread
with its own event loop, which would not work with the async engine the fixtures build. This
client drives the ASGI callable directly in the running loop instead, and emulates the one piece
of server behaviour the app depends on: after the application sends ``websocket.close`` the
transport is gone, so a ``websocket.disconnect`` is delivered back to it.
"""

import asyncio
import json
from typing import Any, Self

from starlette.types import Message

DEFAULT_CLIENT = ("203.0.113.10", 51000)
DEFAULT_TIMEOUT = 3.0
# Frames that may arrive at any moment and are not what a given assertion is waiting for.
NOISE = frozenset({"hosts.update", "ping"})


class WebSocketClosed(Exception):
    def __init__(self, code: int | None) -> None:
        super().__init__(f"websocket closed with {code}")
        self.code = code


class ASGIWebSocket:
    def __init__(
        self,
        app: Any,
        *,
        path: str = "/ws",
        headers: dict[str, str] | None = None,
        client: tuple[str, int] = DEFAULT_CLIENT,
    ) -> None:
        self._app = app
        self._scope: dict[str, Any] = {
            "type": "websocket",
            "asgi": {"version": "3.0", "spec_version": "2.3"},
            "http_version": "1.1",
            "scheme": "ws",
            "server": ("testserver", 80),
            "client": client,
            "root_path": "",
            "path": path,
            "raw_path": path.encode(),
            "query_string": b"",
            "headers": [(b"host", b"testserver")]
            + [(k.lower().encode(), v.encode()) for k, v in (headers or {}).items()],
            "subprotocols": [],
            "state": {},
        }
        self._to_app: asyncio.Queue[Message] = asyncio.Queue()
        self._from_app: asyncio.Queue[Message] = asyncio.Queue()
        self._task: asyncio.Task[None] | None = None
        self._app_error: BaseException | None = None
        self.accepted = False
        self.close_code: int | None = None
        self.hello_ack: dict[str, Any] | None = None
        self.snapshot: dict[str, Any] | None = None

    # -- lifecycle --------------------------------------------------------------------------

    async def open(self, timeout: float = DEFAULT_TIMEOUT) -> Self:
        self._task = asyncio.create_task(self._serve(), name="asgi-ws")
        self._to_app.put_nowait({"type": "websocket.connect"})
        message = await self._next(timeout)
        if message["type"] == "websocket.accept":
            self.accepted = True
        elif message["type"] == "websocket.close":
            self.close_code = message.get("code", 1000)
        else:  # pragma: no cover - defensive
            raise AssertionError(f"unexpected handshake message {message}")
        return self

    async def __aenter__(self) -> Self:
        return await self.open()

    async def __aexit__(self, exc_type: type[BaseException] | None, *_: object) -> None:
        await self.disconnect(raise_app_error=exc_type is None)

    async def disconnect(self, code: int = 1000, *, raise_app_error: bool = True) -> None:
        if self._task is None:
            return
        self._to_app.put_nowait({"type": "websocket.disconnect", "code": code})
        try:
            await asyncio.wait_for(asyncio.shield(self._task), DEFAULT_TIMEOUT)
        except TimeoutError:  # pragma: no cover - a handler that will not stop
            self._task.cancel()
        self._task = None
        if self._app_error is not None and raise_app_error:
            raise self._app_error

    # -- frames -----------------------------------------------------------------------------

    async def send(self, payload: dict[str, Any]) -> None:
        await self.send_text(json.dumps(payload))

    async def send_text(self, text: str) -> None:
        """Raw frame, for the malformed-input cases."""
        self._to_app.put_nowait({"type": "websocket.receive", "text": text})

    async def receive(self, timeout: float = DEFAULT_TIMEOUT) -> dict[str, Any]:
        """The next server frame. Raises :class:`WebSocketClosed` when the server closed."""
        while True:
            message = await self._next(timeout)
            if message["type"] == "websocket.send":
                return json.loads(message["text"])
            if message["type"] == "websocket.close":
                self.close_code = message.get("code", 1000)
                raise WebSocketClosed(self.close_code)

    async def expect(
        self,
        message_type: str,
        *,
        timeout: float = DEFAULT_TIMEOUT,
        skip: frozenset[str] = NOISE,
    ) -> dict[str, Any]:
        """The next frame of ``message_type``, skipping presence noise but failing on anything
        else - an unexpected frame is a bug, not something to swallow."""
        seen: list[str] = []
        while True:
            frame = await self.receive(timeout)
            if frame["type"] == message_type:
                return frame
            seen.append(frame["type"])
            if frame["type"] not in skip:
                raise AssertionError(f"expected {message_type!r}, got {frame!r} (after {seen})")

    async def drain(self, timeout: float = 0.05) -> list[dict[str, Any]]:
        """Everything already queued; used to assert that nothing else was sent."""
        frames = []
        while True:
            try:
                frames.append(await self.receive(timeout))
            except (TimeoutError, WebSocketClosed):
                return frames

    async def wait_closed(self, timeout: float = DEFAULT_TIMEOUT) -> int | None:
        """Drain until the server closes the connection; returns the close code."""
        try:
            while True:
                await self.receive(timeout)
        except WebSocketClosed as exc:
            return exc.code

    # -- ASGI plumbing ----------------------------------------------------------------------

    async def _serve(self) -> None:
        try:
            await self._app(self._scope, self._to_app.get, self._send)
        except BaseException as exc:  # noqa: BLE001 - surfaced by disconnect()/receive()
            self._app_error = exc
        finally:
            self._from_app.put_nowait({"type": "__finished__"})

    async def _send(self, message: Message) -> None:
        self._from_app.put_nowait(message)
        if message["type"] == "websocket.close":
            # What a real server does once it has closed the transport.
            self._to_app.put_nowait(
                {"type": "websocket.disconnect", "code": message.get("code", 1000)}
            )

    async def _next(self, timeout: float) -> Message:
        message = await asyncio.wait_for(self._from_app.get(), timeout)
        if message["type"] == "__finished__":
            if self._app_error is not None:
                raise self._app_error
            raise WebSocketClosed(self.close_code)
        return message
