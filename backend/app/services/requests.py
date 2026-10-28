"""Connection-request lifecycle (docs/ws-protocol.md section 5), server side.

The server is the state machine: a guest asks (``request.create``), the addressed host answers
(``request.accept`` / ``request.reject``), the guest may withdraw (``request.cancel``), and a
timer settles it otherwise (``expired``). Accepting is the only path that creates a session row
plus its ``session_keys`` secret - for offers too: ``app.services.offers`` opens its session
through :func:`open_session` and :func:`announce_session` below.

Only **contacts** can ask each other (ADR-0014). The host's standing trust ("this contact may use
my connection without asking") is held here, on the server: ``request.incoming.trusted`` tells a
host it may answer without a prompt, and when a ``trust_v2`` host does, the grant is checked
again in the same transaction that creates the session.

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
from sqlalchemy.exc import IntegrityError
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.config import Settings, get_settings
from app.db.session import session_scope
from app.models import ConnectionRequest, Device, Presence, Session, SessionKey, User
from app.models.enums import (
    RequestInitiator,
    RequestStatus,
    SecurityEventType,
    SessionRole,
    SessionStatus,
)
from app.schemas.settings import AppSettings
from app.services import allowlist, relay_tokens, security_events, session_flow
from app.services import contacts as contact_service
from app.services import hosts as host_service
from app.services import sessions as session_service
from app.services.app_settings import settings_service
from app.services.effects import SettledRequest
from app.services.request_state import settle
from app.services.session_timer import scheduler
from app.ws import notify
from app.ws.connection_manager import connection_manager
from app.ws.protocol import (
    Capability,
    ErrorCode,
    OfferClosed,
    OfferResult,
    Peer,
    Relay,
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


def offer_timer_key(offer_id: uuid.UUID) -> str:
    return f"offer:{offer_id}"


@dataclass(frozen=True, slots=True)
class Party:
    """One side of a request: the user, the device and the device's presence row."""

    user: User
    device: Device
    presence: Presence | None

    @property
    def public_ip(self) -> str | None:
        return self.presence.public_ip if self.presence is not None else None


async def load_party(db: AsyncSession, user_id: uuid.UUID, device_id: uuid.UUID) -> Party:
    user = await db.get(User, user_id)
    device = await db.get(Device, device_id)
    if user is None or device is None:  # pragma: no cover - foreign keys guarantee both
        raise WsError(ErrorCode.INTERNAL, "Request references an unknown party")
    return Party(user=user, device=device, presence=await db.get(Presence, device_id))


NO_LONGER_PENDING = "No longer pending: it was answered, withdrawn or expired a moment ago"


def validate_duration(duration_min: int, settings: AppSettings, *, ref: str) -> None:
    if not 1 <= duration_min <= settings.max_session_minutes:
        raise WsError(
            ErrorCode.BAD_REQUEST,
            f"duration_min must be between 1 and {settings.max_session_minutes}",
            ref=ref,
        )


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
        validate_duration(duration_min, settings, ref=ref)
        guest = await load_party(db, guest_user_id, guest_device_id)
        if host_device_id == guest_device_id:
            raise WsError(ErrorCode.BAD_REQUEST, "Cannot request your own device", ref=ref)

        await _assert_guest_is_free(db, guest, ref=ref)
        host_row = await _resolve_host(db, host_device_id, guest_user_id=guest_user_id, ref=ref)
        host = await load_party(db, host_row.user_id, host_row.device_id)
        trusted = await contact_service.trusts(db, host.user.id, guest.user.id)

        now = utcnow()
        request = ConnectionRequest(
            guest_user_id=guest.user.id,
            guest_device_id=guest.device.id,
            host_user_id=host.user.id,
            host_device_id=host.device.id,
            requested_minutes=duration_min,
            status=RequestStatus.PENDING,
            initiator=RequestInitiator.GUEST,
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
            guest_user_id=guest.user.id,
            guest_device_id=guest.device.id,
            guest_name=guest.user.display_name,
            guest_device=guest.device.name,
            duration_min=duration_min,
            allowlist_version=allowlist_version,
            expires_at=expires_at,
            trusted=trusted,
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


async def device_is_answering(db: AsyncSession, device_id: uuid.UUID) -> bool:
    """A host device already has a question open - an incoming request or its own offer."""
    busy = await db.scalar(
        select(ConnectionRequest.id).where(
            ConnectionRequest.host_device_id == device_id,
            ConnectionRequest.status == RequestStatus.PENDING,
        )
    )
    return busy is not None


async def _resolve_host(
    db: AsyncSession, host_device_id: uuid.UUID, *, guest_user_id: uuid.UUID, ref: str
) -> host_service.AvailableHost:
    """The host must be exactly what the guest's host list advertises, and still connected."""
    device = await db.get(Device, host_device_id)
    if device is None:
        raise WsError(ErrorCode.NOT_FOUND, "Unknown host device", ref=ref)
    if device.user_id == guest_user_id:
        raise WsError(ErrorCode.BAD_REQUEST, "Cannot request your own device", ref=ref)
    if not await contact_service.are_contacts(db, guest_user_id, device.user_id):
        raise WsError(ErrorCode.NOT_CONTACT, "You can only ask your contacts", ref=ref)
    host = await host_service.get_available_host(db, host_device_id, visible_to=guest_user_id)
    if host is None or not connection_manager.is_connected(host_device_id):
        raise WsError(ErrorCode.HOST_UNAVAILABLE, "Host is not available", ref=ref)
    if await device_is_answering(db, host_device_id):
        raise WsError(ErrorCode.HOST_UNAVAILABLE, "Host is answering another request", ref=ref)
    return host


# ---------------------------------------------------------------- cancel / reject / accept


async def _load_pending(
    db: AsyncSession, request_id: uuid.UUID, *, device_id: uuid.UUID, is_host: bool, ref: str
) -> ConnectionRequest:
    request = await db.get(ConnectionRequest, request_id)
    if request is None or request.initiator != RequestInitiator.GUEST:
        # An offer is answered with offer.* frames, never with request.* ones.
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
        if not await settle(db, request, RequestStatus.CANCELLED):
            raise WsError(ErrorCode.NOT_FOUND, NO_LONGER_PENDING, ref=ref)
        await db.commit()
        guest_device_id, host_device_id = request.guest_device_id, request.host_device_id
    scheduler.cancel(timer_key(request_id))
    assert guest_device_id is not None
    await notify.send_to_device(
        guest_device_id,
        RequestResult(request_id=request_id, accepted=False, reason="cancelled"),
    )
    await notify.send_to_device(host_device_id, RequestExpired(request_id=request_id))


async def reject(*, request_id: uuid.UUID, device_id: uuid.UUID, ref: str) -> None:
    """``request.reject`` from the addressed host -> ``request.result(false, rejected)``."""
    async with session_scope() as db:
        request = await _load_pending(db, request_id, device_id=device_id, is_host=True, ref=ref)
        if not await settle(db, request, RequestStatus.REJECTED):
            raise WsError(ErrorCode.NOT_FOUND, NO_LONGER_PENDING, ref=ref)
        await db.commit()
        guest_device_id = request.guest_device_id
    scheduler.cancel(timer_key(request_id))
    assert guest_device_id is not None
    await notify.send_to_device(
        guest_device_id,
        RequestResult(request_id=request_id, accepted=False, reason="rejected"),
    )


async def accept(
    *,
    request_id: uuid.UUID,
    device_id: uuid.UUID,
    ref: str,
    runtime: Settings | None = None,
    auto: bool = False,
    verify_trust: bool = False,
) -> Session:
    """``request.accept`` from the addressed host: create the session and its secret, then tell
    the guest (``request.result``) and both parties (``session.created``).

    ``settings`` here is the operator-editable row set in ``app_settings``; ``runtime`` is the
    process configuration from the environment. Only the second knows about the relay, and the
    two are named apart because reaching for the wrong one reads correct and is not.

    ``auto`` is the host saying it answered without a prompt (section 5a). For a host that
    declared ``trust_v2`` (``verify_trust``) the server holds the grant that justifies it, so it
    checks it here, in the transaction that creates the session: a grant withdrawn a moment ago
    - from another of the host's devices - must not let one more session through. The request
    then stays pending and the host is told ``trust_revoked``, so it can still ask its user.

    An older host keeps its rules on its own disk; the server cannot check those, and records
    that it could not (``grant: client_legacy``). Either way the acceptance is written to the
    audit trail, because an acceptance nobody watched is the one an operator will want to find."""
    runtime = runtime or get_settings()
    async with session_scope() as db:
        settings = await settings_service.get(db)
        request = await _load_pending(db, request_id, device_id=device_id, is_host=True, ref=ref)
        assert request.guest_device_id is not None
        guest = await load_party(db, request.guest_user_id, request.guest_device_id)
        host = await load_party(db, request.host_user_id, request.host_device_id)
        if not connection_manager.is_connected(guest.device.id):
            raise WsError(ErrorCode.NOT_FOUND, "The guest is no longer connected", ref=ref)
        grant: str | None = None
        if auto:
            grant = "client_legacy"
            if verify_trust:
                if not await contact_service.trusts(db, host.user.id, guest.user.id):
                    raise WsError(
                        ErrorCode.TRUST_REVOKED,
                        "This contact is no longer allowed without asking",
                        ref=ref,
                    )
                grant = "server"
        session, key = await open_session(db, request, guest, host, grant=grant, ref=ref)
        await db.commit()
        announcement = await announcement_for(db, session, key)

    scheduler.cancel(timer_key(request_id))
    await notify.send_to_device(
        guest.device.id,
        RequestResult(request_id=request.id, accepted=True, reason=None, session_id=session.id),
    )
    await announce_session(
        session,
        announcement,
        guest=guest,
        host=host,
        runtime=runtime,
        settings=settings,
        initiator=RequestInitiator.GUEST,
    )
    return session


# ---------------------------------------------------------------- the session itself


@dataclass(frozen=True, slots=True)
class Announcement:
    secret_b64: str
    allowlist_version: int
    expires_at: datetime


async def open_session(
    db: AsyncSession,
    request: ConnectionRequest,
    guest: Party,
    host: Party,
    *,
    grant: str | None,
    ref: str,
) -> tuple[Session, SessionKey]:
    """Mark ``request`` accepted and insert the session plus its secret. Flushes; the caller
    commits and then calls :func:`announce_session`. ``grant`` is set when nobody was asked.

    The acceptance is a conditional claim (:func:`app.services.request_state.settle`): if a
    cancel, reject, expiry or another device's acceptance got there first, this one answers
    ``not_found`` and opens nothing. ``guest_device_id`` is written by the same claim, which is
    what makes "the first device of the guest to accept an offer" well defined."""
    # The partial unique indexes enforce this too; checking first turns a would-be
    # IntegrityError into the documented error code.
    for device in (guest.device, host.device):
        if await session_service.get_live_for_device(db, device.id) is not None:
            raise WsError(ErrorCode.SESSION_EXISTS, "A session is already live", ref=ref)
    for user in (guest.user, host.user):
        if await session_service.get_live_for_user(db, user.id) is not None:
            raise WsError(ErrorCode.SESSION_EXISTS, "A session is already live", ref=ref)

    if not await settle(
        db,
        request,
        RequestStatus.ACCEPTED,
        auto_accepted=grant is not None,
        guest_device_id=guest.device.id,
    ):
        raise WsError(ErrorCode.NOT_FOUND, NO_LONGER_PENDING, ref=ref)
    now = utcnow()
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
    try:
        await db.flush()
    except IntegrityError as exc:
        # Another request for one of these four parties opened its session in the same instant;
        # the partial unique indexes kept the second one out.
        raise WsError(ErrorCode.SESSION_EXISTS, "A session is already live", ref=ref) from exc
    # Never logged: the tunnel pre-shared secret, deleted again by ``end_session``.
    key = SessionKey(session_id=session.id, secret=secrets.token_bytes(SECRET_BYTES))
    db.add(key)
    if grant is not None:
        # Attributed to the host: it is the host's standing grant that answered, and the row is
        # what lets an operator (or the host itself) audit who that grant let in.
        security_events.record_event(
            db,
            SecurityEventType.REQUEST_AUTO_ACCEPTED,
            user_id=host.user.id,
            device_id=host.device.id,
            details={
                "request_id": str(request.id),
                "session_id": str(session.id),
                "guest_user_id": str(guest.user.id),
                "guest_device_id": str(guest.device.id),
                "guest_display_name": guest.user.display_name,
                "guest_device_name": guest.device.name,
                "duration_min": request.requested_minutes,
                "grant": grant,
            },
        )
    await db.flush()
    return session, key


async def announcement_for(db: AsyncSession, session: Session, key: SessionKey) -> Announcement:
    """What both ``session.created`` frames share. Read inside the transaction, sent after it."""
    expires_at = ensure_utc(session.expires_at)
    assert expires_at is not None
    return Announcement(
        secret_b64=base64.b64encode(key.secret).decode("ascii"),
        allowlist_version=await allowlist.current_version(db),
        expires_at=expires_at,
    )


async def announce_session(
    session: Session,
    announcement: Announcement,
    *,
    guest: Party,
    host: Party,
    runtime: Settings,
    settings: AppSettings,
    initiator: RequestInitiator,
) -> None:
    """``session.created`` to both parties, the host list, and the session's two deadlines."""
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
                secret_b64=announcement.secret_b64,
                expires_at=announcement.expires_at,
                allowlist_version=announcement.allowlist_version,
                peer_public_ip=peer.public_ip or "",
                same_public_ip=same_public_ip,
                peer=Peer(
                    user_id=peer.user.id,
                    device_id=peer.device.id,
                    user_display_name=peer.user.display_name,
                    device_name=peer.device.name,
                ),
                # One token per party, minted here and never stored: the relay verifies the
                # signature rather than looking the session up, so nothing has to persist.
                relay=(
                    Relay(
                        address=runtime.relay_host,
                        port=runtime.relay_port,
                        token=relay_tokens.mint(runtime, session.id, role),
                    )
                    if runtime.relay_enabled
                    else None
                ),
                initiator=initiator.value,
            ),
        )
    # The host is busy now, so it leaves everybody's host list.
    async with session_scope() as db:
        await notify.broadcast_hosts_update(db)
    # Section 5: the connect deadline runs from ``session.created``, the expiry from the row's
    # ``expires_at``. Both are cancelled by ``sessions.end_session``, whatever ends the session.
    session_flow.schedule_timers(session, connect_timeout_seconds=settings.connect_timeout_seconds)


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
        if (
            request is None
            or request.status != RequestStatus.PENDING
            or request.initiator != RequestInitiator.GUEST
        ):
            return False
        if not await settle(db, request, RequestStatus.EXPIRED):
            return False
        await db.commit()
        guest_device_id, host_device_id = request.guest_device_id, request.host_device_id
    assert guest_device_id is not None
    await notify.send_to_device(
        guest_device_id,
        RequestResult(request_id=request_id, accepted=False, reason="expired"),
    )
    await notify.send_to_device(host_device_id, RequestExpired(request_id=request_id))
    return True


async def cancel_for_device(db: AsyncSession, device_id: uuid.UUID) -> int:
    """The device's control channel dropped: settle every request and offer it was part of.

    Guest gone -> the host's window is closed with ``request.expired``. Host gone -> the guest
    learns ``request.result(false, host_unavailable)``, or for an offer every one of the guest's
    devices is told ``offer.closed(host_unavailable)``. An offer is addressed to a guest *user*,
    so one of their devices leaving does not withdraw it. Commits."""
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
    settled = []
    for request in rows:
        if not await settle(db, request, RequestStatus.CANCELLED):
            continue  # answered a moment ago by the other side; its own frames went out
        settled.append(
            SettledRequest(
                request_id=request.id,
                initiator=request.initiator,
                guest_user_id=request.guest_user_id,
                guest_device_id=request.guest_device_id,
                host_device_id=request.host_device_id,
                reason="host_unavailable" if request.host_device_id == device_id else "cancelled",
            )
        )
    await db.commit()
    await announce_settled(settled)
    return len(settled)


async def announce_settled(items: list[SettledRequest]) -> None:
    """Close both sides' windows for requests and offers settled without a session.

    The departed party, if any, is no longer connected, so the frame meant for it is simply not
    delivered; one rule covers every cause."""
    for item in items:
        if item.initiator == RequestInitiator.GUEST:
            scheduler.cancel(timer_key(item.request_id))
            if item.guest_device_id is not None:
                await notify.send_to_device(
                    item.guest_device_id,
                    RequestResult(request_id=item.request_id, accepted=False, reason=item.reason),
                )
            await notify.send_to_device(
                item.host_device_id, RequestExpired(request_id=item.request_id)
            )
        else:
            scheduler.cancel(offer_timer_key(item.request_id))
            await notify.send_to_device(
                item.host_device_id,
                OfferResult(offer_id=item.request_id, accepted=False, reason=item.reason),
            )
            await notify.send_to_user(
                item.guest_user_id,
                OfferClosed(offer_id=item.request_id, reason=item.reason),
                capability=Capability.OFFERS,
            )
