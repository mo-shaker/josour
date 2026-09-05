"""Connection-request lifecycle (docs/ws-protocol.md section 5), server side.

The server is the state machine: a guest asks (``request.create``), the addressed host answers
(``request.accept`` / ``request.reject``), the guest may withdraw (``request.cancel``), and a
timer settles it otherwise (``expired``). Accepting is the only path that creates a session row
plus its ``session_keys`` secret.

Unlike the REST services these entry points **own their transaction**: they are driven by frames
and timers rather than by a request/response cycle, and the outgoing frames (and the expiry
timer) must never be observable before the row that justifies them is committed.
"""

import base64
import logging
import secrets
import uuid
from dataclasses import dataclass
from datetime import datetime, timedelta

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.db.session import session_scope
from app.models import ConnectionRequest, Device, Presence, Session, SessionKey, User
from app.models.enums import RequestStatus, SessionRole, SessionStatus
from app.services import allowlist, session_flow
from app.services import hosts as host_service
from app.services import sessions as session_service
from app.services.app_settings import settings_service
from app.services.session_timer import scheduler
from app.ws import notify
from app.ws.connection_manager import connection_manager
from app.ws.protocol import (
    ErrorCode,
    Peer,
    RequestCreated,
    RequestExpired,
    RequestIncoming,
    RequestResult,
    SessionCreated,
    WsError,
)

log = logging.getLogger(__name__)

SECRET_BYTES = 32
"""``session.created.secret_b64`` is 32 raw bytes, Base64 encoded (section 4)."""


def timer_key(request_id: uuid.UUID) -> str:
    return f"request:{request_id}"


@dataclass(frozen=True, slots=True)
class Party:
    """One side of a request: the user, the device and the device's presence row."""

    user: User
    device: Device
    presence: Presence | None

    @property
    def public_ip(self) -> str | None:
        return self.presence.public_ip if self.presence is not None else None


async def _load_party(db: AsyncSession, user_id: uuid.UUID, device_id: uuid.UUID) -> Party:
    user = await db.get(User, user_id)
    device = await db.get(Device, device_id)
    if user is None or device is None:  # pragma: no cover - foreign keys guarantee both
        raise WsError(ErrorCode.INTERNAL, "Request references an unknown party")
    return Party(user=user, device=device, presence=await db.get(Presence, device_id))


# ---------------------------------------------------------------- create


async def create(
    *,
    guest_user_id: uuid.UUID,
    guest_device_id: uuid.UUID,
    host_device_id: uuid.UUID,
    duration_min: int,
    ref: str,
) -> ConnectionRequest:
    """``request.create``: validate, persist ``pending``, answer the guest and ring the host."""
    async with session_scope() as db:
        settings = await settings_service.get(db)
        if not 1 <= duration_min <= settings.max_session_minutes:
            raise WsError(
                ErrorCode.BAD_REQUEST,
                f"duration_min must be between 1 and {settings.max_session_minutes}",
                ref=ref,
            )
        guest = await _load_party(db, guest_user_id, guest_device_id)
        if host_device_id == guest_device_id:
            raise WsError(ErrorCode.BAD_REQUEST, "Cannot request your own device", ref=ref)

        await _assert_guest_is_free(db, guest, ref=ref)
        host_row = await _resolve_host(db, host_device_id, guest_user_id=guest_user_id, ref=ref)
        host = await _load_party(db, host_row.user_id, host_row.device_id)

        now = utcnow()
        request = ConnectionRequest(
            guest_user_id=guest.user.id,
            guest_device_id=guest.device.id,
            host_user_id=host.user.id,
            host_device_id=host.device.id,
            requested_minutes=duration_min,
            status=RequestStatus.PENDING,
            created_at=now,
            expires_at=now + timedelta(seconds=settings.request_timeout_seconds),
        )
        db.add(request)
        await db.commit()

        expires_at = ensure_utc(request.expires_at)
        assert expires_at is not None
        allowlist_version = await allowlist.current_version(db)

    await notify.send_to_device(
        guest.device.id,
        RequestCreated(ref=ref, request_id=request.id, expires_at=expires_at),
    )
    await notify.send_to_device(
        host.device.id,
        RequestIncoming(
            request_id=request.id,
            guest_name=guest.user.display_name,
            guest_device=guest.device.name,
            duration_min=duration_min,
            allowlist_version=allowlist_version,
            expires_at=expires_at,
        ),
    )
    _schedule_expiry(request.id, expires_at)
    return request


async def _assert_guest_is_free(db: AsyncSession, guest: Party, *, ref: str) -> None:
    """Section 5: one live session per user and per device, one pending request at a time."""
    if await session_service.get_live_for_device(db, guest.device.id) is not None:
        raise WsError(ErrorCode.SESSION_EXISTS, "This device already has a session", ref=ref)
    if await session_service.get_live_for_user(db, guest.user.id) is not None:
        raise WsError(ErrorCode.SESSION_EXISTS, "You already have a session", ref=ref)
    pending = await db.scalar(
        select(ConnectionRequest.id).where(
            ConnectionRequest.guest_device_id == guest.device.id,
            ConnectionRequest.status == RequestStatus.PENDING,
        )
    )
    if pending is not None:
        raise WsError(ErrorCode.REQUEST_PENDING, "A request is already pending", ref=ref)


async def _resolve_host(
    db: AsyncSession, host_device_id: uuid.UUID, *, guest_user_id: uuid.UUID, ref: str
) -> host_service.AvailableHost:
    """The host must be exactly what the host list advertises, and still connected."""
    if await db.get(Device, host_device_id) is None:
        raise WsError(ErrorCode.NOT_FOUND, "Unknown host device", ref=ref)
    host = await host_service.get_available_host(db, host_device_id, exclude_user_id=guest_user_id)
    if host is None or not connection_manager.is_connected(host_device_id):
        raise WsError(ErrorCode.HOST_UNAVAILABLE, "Host is not available", ref=ref)
    busy = await db.scalar(
        select(ConnectionRequest.id).where(
            ConnectionRequest.host_device_id == host_device_id,
            ConnectionRequest.status == RequestStatus.PENDING,
        )
    )
    if busy is not None:
        raise WsError(ErrorCode.HOST_UNAVAILABLE, "Host is answering another request", ref=ref)
    return host


# ---------------------------------------------------------------- cancel / reject / accept


async def _load_pending(
    db: AsyncSession, request_id: uuid.UUID, *, device_id: uuid.UUID, is_host: bool, ref: str
) -> ConnectionRequest:
    request = await db.get(ConnectionRequest, request_id)
    if request is None:
        raise WsError(ErrorCode.NOT_FOUND, "Unknown request", ref=ref)
    owner = request.host_device_id if is_host else request.guest_device_id
    if owner != device_id:
        raise WsError(ErrorCode.FORBIDDEN, "This request is not addressed to you", ref=ref)
    if request.status != RequestStatus.PENDING:
        raise WsError(ErrorCode.NOT_FOUND, "Request is no longer pending", ref=ref)
    return request


async def cancel(*, request_id: uuid.UUID, device_id: uuid.UUID, ref: str) -> None:
    """``request.cancel`` from the guest. The host's dialog is closed with ``request.expired``,
    the only server frame the contract gives for that (section 4)."""
    async with session_scope() as db:
        request = await _load_pending(db, request_id, device_id=device_id, is_host=False, ref=ref)
        request.status = RequestStatus.CANCELLED
        request.responded_at = utcnow()
        await db.commit()
        guest_device_id, host_device_id = request.guest_device_id, request.host_device_id
    scheduler.cancel(timer_key(request_id))
    await notify.send_to_device(
        guest_device_id,
        RequestResult(request_id=request_id, accepted=False, reason="cancelled"),
    )
    await notify.send_to_device(host_device_id, RequestExpired(request_id=request_id))


async def reject(*, request_id: uuid.UUID, device_id: uuid.UUID, ref: str) -> None:
    """``request.reject`` from the addressed host -> ``request.result(false, rejected)``."""
    async with session_scope() as db:
        request = await _load_pending(db, request_id, device_id=device_id, is_host=True, ref=ref)
        request.status = RequestStatus.REJECTED
        request.responded_at = utcnow()
        await db.commit()
        guest_device_id = request.guest_device_id
    scheduler.cancel(timer_key(request_id))
    await notify.send_to_device(
        guest_device_id,
        RequestResult(request_id=request_id, accepted=False, reason="rejected"),
    )


async def accept(*, request_id: uuid.UUID, device_id: uuid.UUID, ref: str) -> Session:
    """``request.accept`` from the addressed host: create the session and its secret, then tell
    the guest (``request.result``) and both parties (``session.created``)."""
    async with session_scope() as db:
        settings = await settings_service.get(db)
        request = await _load_pending(db, request_id, device_id=device_id, is_host=True, ref=ref)
        guest = await _load_party(db, request.guest_user_id, request.guest_device_id)
        host = await _load_party(db, request.host_user_id, request.host_device_id)
        if not connection_manager.is_connected(guest.device.id):
            raise WsError(ErrorCode.NOT_FOUND, "The guest is no longer connected", ref=ref)
        # The partial unique indexes enforce this too; checking first turns a would-be
        # IntegrityError into the documented error code.
        for device in (guest.device, host.device):
            if await session_service.get_live_for_device(db, device.id) is not None:
                raise WsError(ErrorCode.SESSION_EXISTS, "A session is already live", ref=ref)

        now = utcnow()
        request.status = RequestStatus.ACCEPTED
        request.responded_at = now
        session = Session(
            request_id=request.id,
            guest_user_id=guest.user.id,
            guest_device_id=guest.device.id,
            host_user_id=host.user.id,
            host_device_id=host.device.id,
            status=SessionStatus.CONNECTING,
            created_at=now,
            expires_at=now + timedelta(minutes=request.requested_minutes),
        )
        db.add(session)
        await db.flush()
        # Never logged: the tunnel pre-shared secret, deleted again by ``end_session``.
        key = SessionKey(session_id=session.id, secret=secrets.token_bytes(SECRET_BYTES))
        db.add(key)
        await db.commit()

        secret_b64 = base64.b64encode(key.secret).decode("ascii")
        allowlist_version = await allowlist.current_version(db)
        expires_at = ensure_utc(session.expires_at)
        assert expires_at is not None

    scheduler.cancel(timer_key(request_id))
    await notify.send_to_device(
        guest.device.id,
        RequestResult(request_id=request.id, accepted=True, reason=None, session_id=session.id),
    )
    same_public_ip = guest.public_ip is not None and guest.public_ip == host.public_ip
    for me, peer, role in (
        (guest, host, SessionRole.GUEST),
        (host, guest, SessionRole.HOST),
    ):
        await notify.send_to_device(
            me.device.id,
            SessionCreated(
                session_id=session.id,
                role=role.value,
                secret_b64=secret_b64,
                expires_at=expires_at,
                allowlist_version=allowlist_version,
                peer_public_ip=peer.public_ip or "",
                same_public_ip=same_public_ip,
                peer=Peer(user_display_name=peer.user.display_name, device_name=peer.device.name),
            ),
        )
    # The host is busy now, so it leaves everybody's host list.
    async with session_scope() as db:
        await notify.broadcast_hosts_update(db)
    # Section 5: the connect deadline runs from ``session.created``, the expiry from the row's
    # ``expires_at``. Both are cancelled by ``sessions.end_session``, whatever ends the session.
    session_flow.schedule_timers(session, connect_timeout_seconds=settings.connect_timeout_seconds)
    return session


# ---------------------------------------------------------------- expiry / disconnect


def _schedule_expiry(request_id: uuid.UUID, expires_at: datetime) -> None:
    async def fire() -> None:
        await expire(request_id)

    scheduler.schedule_at(timer_key(request_id), expires_at, fire)


async def expire(request_id: uuid.UUID) -> bool:
    """Timer callback at ``expires_at``: ``request.result(false, expired)`` to the guest and
    ``request.expired`` to the host, so both windows close."""
    async with session_scope() as db:
        request = await db.get(ConnectionRequest, request_id)
        if request is None or request.status != RequestStatus.PENDING:
            return False
        request.status = RequestStatus.EXPIRED
        request.responded_at = utcnow()
        await db.commit()
        guest_device_id, host_device_id = request.guest_device_id, request.host_device_id
    await notify.send_to_device(
        guest_device_id,
        RequestResult(request_id=request_id, accepted=False, reason="expired"),
    )
    await notify.send_to_device(host_device_id, RequestExpired(request_id=request_id))
    return True


async def cancel_for_device(db: AsyncSession, device_id: uuid.UUID) -> int:
    """The device's control channel dropped: settle every request it was part of.

    Guest gone -> the host's window is closed with ``request.expired``. Host gone -> the guest
    learns ``request.result(false, host_unavailable)``. Commits."""
    rows = list(
        await db.scalars(
            select(ConnectionRequest).where(
                ConnectionRequest.status == RequestStatus.PENDING,
                (ConnectionRequest.guest_device_id == device_id)
                | (ConnectionRequest.host_device_id == device_id),
            )
        )
    )
    if not rows:
        return 0
    now = utcnow()
    for request in rows:
        request.status = RequestStatus.CANCELLED
        request.responded_at = now
    await db.commit()
    for request in rows:
        scheduler.cancel(timer_key(request.id))
        if request.guest_device_id == device_id:
            await notify.send_to_device(
                request.host_device_id, RequestExpired(request_id=request.id)
            )
        else:
            await notify.send_to_device(
                request.guest_device_id,
                RequestResult(request_id=request.id, accepted=False, reason="host_unavailable"),
            )
    return len(rows)
