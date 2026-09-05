"""``GET /ws`` - the control channel (docs/ws-protocol.md).

The handler stays thin on purpose: accept, authenticate the ``hello``, register the connection,
then loop parse -> service -> send. All state lives in ``app.services`` and in the
``ConnectionManager``; all frames are built in ``app.ws.protocol``.
"""

import asyncio
import ipaddress
import logging
import time

from fastapi import APIRouter, WebSocket
from sqlalchemy.ext.asyncio import AsyncSession
from starlette.websockets import WebSocketDisconnect

from app.core.clock import utcnow
from app.core.config import Settings, get_settings
from app.core.security import InvalidAccessToken, decode_access_token
from app.db.session import session_scope
from app.models import Device, User
from app.models.enums import DeviceStatus
from app.services import allowlist, presence, requests, session_flow
from app.services import sessions as session_service
from app.services.app_settings import settings_service
from app.services.events import event_bus
from app.ws import notify
from app.ws.connection_manager import Connection, connection_manager
from app.ws.protocol import (
    ClientMessage,
    ClientPing,
    ClientPong,
    CloseCode,
    ErrorCode,
    ErrorFrame,
    Hello,
    HelloAck,
    HostAvailable,
    Ping,
    Pong,
    RequestAccept,
    RequestCancel,
    RequestCreate,
    RequestReject,
    ServerSettings,
    SessionConnected,
    SessionConnectFailed,
    SessionEnd,
    SessionEndpoint,
    SessionStats,
    WsError,
    message_ref,
    parse_client_message,
)

log = logging.getLogger(__name__)

router = APIRouter()

HELLO_TIMEOUT_SECONDS = 5.0
"""Section 1: the first frame must be a valid ``hello`` within 5 s, else close 4401."""
PING_INTERVAL_SECONDS = 20.0
DEAD_AFTER_SECONDS = 40.0
"""Two missed beats and the peer counts as disconnected."""


# ---------------------------------------------------------------- helpers


def ws_client_ip(websocket: WebSocket) -> str | None:
    """The client address as the server sees it, used for ``public_ip`` and the probe target.

    Behind the reverse proxy (Caddy, private network - see the Dockerfile CMD, which runs
    uvicorn with ``--proxy-headers``) the peer address is the proxy, so the left-most
    ``X-Forwarded-For`` entry is the real client. The header is only trusted when the peer is
    itself a private/loopback address, so a directly connected client cannot forge it."""
    peer = websocket.client.host if websocket.client else None
    forwarded = websocket.headers.get("x-forwarded-for")
    if forwarded and (peer is None or _is_local_peer(peer)):
        candidate = forwarded.split(",")[0].strip()
        try:
            return str(ipaddress.ip_address(candidate))
        except ValueError:
            log.debug("ignoring malformed x-forwarded-for")
    return peer


def _is_local_peer(peer: str) -> bool:
    try:
        address = ipaddress.ip_address(peer)
    except ValueError:
        return False
    return address.is_private or address.is_loopback


async def _close(websocket: WebSocket, code: CloseCode) -> None:
    try:
        await websocket.close(code=int(code))
    except (RuntimeError, WebSocketDisconnect, OSError):  # pragma: no cover - peer already gone
        pass


async def _send_raw(websocket: WebSocket, message: ErrorFrame) -> None:
    try:
        await websocket.send_text(message.to_json())
    except (RuntimeError, WebSocketDisconnect, OSError):  # pragma: no cover - peer already gone
        pass


# ---------------------------------------------------------------- handshake


class _HandshakeRejected(Exception):
    def __init__(self, code: CloseCode, reason: str) -> None:
        super().__init__(reason)
        self.code = code
        self.reason = reason


async def _read_hello(websocket: WebSocket) -> Hello:
    try:
        raw = await asyncio.wait_for(websocket.receive_text(), HELLO_TIMEOUT_SECONDS)
    except TimeoutError as exc:
        raise _HandshakeRejected(CloseCode.NO_HELLO, "no hello within the deadline") from exc
    except (KeyError, WebSocketDisconnect, RuntimeError) as exc:
        raise _HandshakeRejected(CloseCode.NO_HELLO, "connection closed before hello") from exc
    try:
        message = parse_client_message(raw)
    except WsError as exc:
        await _send_raw(websocket, exc.to_message())
        raise _HandshakeRejected(CloseCode.NO_HELLO, "first frame is not a valid hello") from exc
    if not isinstance(message, Hello):
        await _send_raw(
            websocket,
            ErrorFrame(code=ErrorCode.UNAUTHORIZED, message="The first frame must be hello"),
        )
        raise _HandshakeRejected(CloseCode.NO_HELLO, f"first frame was {message.type}")
    return message


async def _authenticate(db: AsyncSession, settings: Settings, hello: Hello) -> tuple[User, Device]:
    """4401 for anything that makes the ``hello`` invalid, 4403 for a rejected identity."""
    try:
        claims = decode_access_token(settings, hello.token)
    except InvalidAccessToken as exc:
        raise _HandshakeRejected(CloseCode.NO_HELLO, "invalid access token") from exc
    if claims.device_id != hello.device_id:
        raise _HandshakeRejected(CloseCode.NO_HELLO, "device_id does not match the token")
    user = await db.get(User, claims.user_id)
    if user is None:
        raise _HandshakeRejected(CloseCode.NO_HELLO, "unknown user")
    device = await db.get(Device, claims.device_id)
    if device is None or device.user_id != user.id:
        raise _HandshakeRejected(CloseCode.NO_HELLO, "unknown device")
    if not user.is_active:
        raise _HandshakeRejected(CloseCode.NOT_ALLOWED, "user is disabled")
    if device.status == DeviceStatus.REVOKED:
        raise _HandshakeRejected(CloseCode.NOT_ALLOWED, "device is revoked")
    return user, device


async def _handshake(websocket: WebSocket) -> Connection | None:
    """Returns the registered connection, or ``None`` after closing with 4401/4403."""
    connection: Connection | None = None
    try:
        hello = await _read_hello(websocket)
        settings: Settings = getattr(websocket.app.state, "settings", None) or get_settings()
        public_ip = ws_client_ip(websocket)
        async with session_scope() as db:
            user, device = await _authenticate(db, settings, hello)
            connection = Connection(
                websocket, device_id=device.id, user_id=user.id, public_ip=public_ip
            )
            await connection_manager.register(connection)
            await presence.mark_connected(
                db, device.id, public_ip=public_ip, diagnostics=hello.diagnostics
            )
            device.last_seen_at = utcnow()
            await db.commit()
            app_settings = await settings_service.get(db)
            await connection.send(
                HelloAck(
                    server_time=utcnow(),
                    public_ip=public_ip or "",
                    settings=ServerSettings(
                        max_session_minutes=app_settings.max_session_minutes,
                        request_timeout_seconds=app_settings.request_timeout_seconds,
                        allowed_ports=list(app_settings.allowed_ports),
                        log_domains=app_settings.log_domains,
                    ),
                    allowlist_version=await allowlist.current_version(db),
                )
            )
            await notify.send_hosts_snapshot(db, connection)
            await notify.broadcast_hosts_update(db)
    except _HandshakeRejected as exc:
        log.info("ws handshake rejected", extra={"code": int(exc.code), "reason": exc.reason})
        await _close(websocket, exc.code)
        return None
    except Exception:
        log.exception("ws handshake failed")
        if connection is not None:
            await connection_manager.unregister(connection)
        await _close(websocket, CloseCode.INTERNAL_ERROR)
        return None
    log.info(
        "ws connected", extra={"device_id": str(connection.device_id), "app": hello.app_version}
    )
    return connection


# ---------------------------------------------------------------- message handling


async def _handle(connection: Connection, message: ClientMessage) -> None:
    match message:
        case ClientPing():
            await connection.send(Pong())
        case ClientPong():
            connection.mark_pong()
        case HostAvailable():
            await _handle_host_available(connection, message)
        case RequestCreate():
            await requests.create(
                guest_user_id=connection.user_id,
                guest_device_id=connection.device_id,
                host_device_id=message.host_device_id,
                duration_min=message.duration_min,
                ref=message.ref,
            )
        case RequestCancel():
            await requests.cancel(
                request_id=message.request_id, device_id=connection.device_id, ref=message.ref
            )
        case RequestAccept():
            await requests.accept(
                request_id=message.request_id, device_id=connection.device_id, ref=message.ref
            )
        case RequestReject():
            await requests.reject(
                request_id=message.request_id, device_id=connection.device_id, ref=message.ref
            )
        case SessionEndpoint():
            await session_flow.endpoint(device_id=connection.device_id, message=message)
        case SessionConnected():
            await session_flow.connected(device_id=connection.device_id, message=message)
        case SessionConnectFailed():
            await session_flow.connect_failed(
                device_id=connection.device_id, user_id=connection.user_id, message=message
            )
        case SessionStats():
            await session_flow.stats(device_id=connection.device_id, message=message)
        case SessionEnd():
            await session_flow.end(device_id=connection.device_id, message=message)
        case Hello():
            raise WsError(ErrorCode.BAD_REQUEST, "hello is only valid as the first frame")
        case _:  # pragma: no cover - the union above is exhaustive
            raise WsError(ErrorCode.BAD_REQUEST, f"Unsupported message type '{message.type}'")


async def _handle_host_available(connection: Connection, message: HostAvailable) -> None:
    async with session_scope() as db:
        await presence.set_host_available(
            db,
            connection.device_id,
            available=message.available,
            listen_port=message.listen_port,
        )
        await db.commit()
        await notify.broadcast_hosts_update(db)
    if message.available and message.listen_port is not None:
        # Section 6: a background TCP probe; the handler never waits for its 3 s timeout.
        presence.schedule_probe(connection.device_id, connection.public_ip, message.listen_port)


async def _dispatch(connection: Connection, raw: str) -> None:
    try:
        message = parse_client_message(raw)
    except WsError as exc:
        await connection.send(exc.to_message())
        return
    try:
        await _handle(connection, message)
    except WsError as exc:
        await connection.send(exc.to_message(message_ref(message)))
    except Exception:
        log.exception(
            "ws handler failed",
            extra={"type": message.type, "device_id": str(connection.device_id)},
        )
        await connection.send(
            ErrorFrame(
                ref=message_ref(message),
                code=ErrorCode.INTERNAL,
                message="Internal server error",
            )
        )


# ---------------------------------------------------------------- loops


async def _read_loop(connection: Connection) -> None:
    while True:
        try:
            raw = await connection.websocket.receive_text()
        except (WebSocketDisconnect, RuntimeError):
            return
        except KeyError:
            await connection.send(
                ErrorFrame(code=ErrorCode.BAD_REQUEST, message="Only text frames are accepted")
            )
            continue
        await _dispatch(connection, raw)


async def _heartbeat(connection: Connection) -> None:
    """Section 1: ``ping`` every 20 s; no ``pong`` for 40 s means the peer is gone."""
    while not connection.closed.is_set():
        await asyncio.sleep(max(PING_INTERVAL_SECONDS / 4, 0.01))
        now = time.monotonic()
        if now - connection.last_pong_at >= DEAD_AFTER_SECONDS:
            log.info("ws heartbeat timeout", extra={"device_id": str(connection.device_id)})
            await connection.close(CloseCode.GOING_AWAY)
            return
        if now - connection.last_ping_at >= PING_INTERVAL_SECONDS:
            connection.last_ping_at = now
            if not await connection.send(Ping()):
                return


async def _run(connection: Connection) -> None:
    tasks = {
        asyncio.create_task(_read_loop(connection), name="ws-read"),
        asyncio.create_task(_heartbeat(connection), name="ws-heartbeat"),
        asyncio.create_task(connection.closed.wait(), name="ws-closed"),
    }
    try:
        await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)


async def _teardown(connection: Connection) -> None:
    """Any disconnect cause, one path (section 1). Skipped entirely when this connection was
    already superseded by a newer one for the same device - that one owns the state now."""
    if not await connection_manager.unregister(connection):
        await connection.close(CloseCode.SUPERSEDED)
        return
    try:
        async with session_scope() as db:
            await presence.mark_disconnected(db, connection.device_id)
            await db.commit()
            await requests.cancel_for_device(db, connection.device_id)
            events = await session_service.end_device_sessions(db, connection.device_id)
            await db.commit()
            for event in events:
                await event_bus.publish(event)
            await notify.broadcast_hosts_update(db)
    except Exception:  # pragma: no cover - cleanup must never escape
        log.exception("ws teardown failed", extra={"device_id": str(connection.device_id)})
    await connection.close(CloseCode.NORMAL)
    log.info("ws disconnected", extra={"device_id": str(connection.device_id)})


@router.websocket("/ws")
async def control_channel(websocket: WebSocket) -> None:
    await websocket.accept()
    connection = await _handshake(websocket)
    if connection is None:
        return
    try:
        await _run(connection)
    finally:
        await _teardown(connection)
