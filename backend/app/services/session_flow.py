"""Session lifecycle over the control channel (docs/ws-protocol.md sections 3-5).

Where ``app.services.requests`` stops - the session row exists, both parties hold
``session.created`` - this module takes over and drives the rest of the state machine:

``connecting``
  the two parties exchange ``session.endpoint`` (the server stores each side in ``session_keys``
  and forwards it as ``session.peer_endpoint``), then the **host** reports ``session.connected``
  (-> ``active``) or either party reports ``session.connect_failed`` (-> ``ended``).
``active``
  the host reports ``session.stats``; either party may ``session.end``.

Two deadlines back this up, both on the shared scheduler: the connect deadline
(``connect_timeout_seconds`` from ``session.created``) and the session's ``expires_at``.

Every path that ends a session calls ``sessions.end_session``, which is what deletes the
``session_keys`` row, cancels both timers and produces the ``SessionEnded`` event this module
publishes after committing. Like ``app.services.requests``, these entry points own their
transaction: they are driven by frames and timers, not by a request/response cycle.

Authorisation, applied uniformly by :func:`_party`: only the two devices of a session may say
anything about it (otherwise ``forbidden``), an unknown or already ended session is ``not_found``,
and a frame that arrives in a live-but-wrong state is ``bad_request``.
"""

import logging
import uuid
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Any

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.errors import ValidationFailed
from app.db.session import session_scope
from app.models import Session, SessionDomain, SessionKey
from app.models.enums import (
    NON_ENDED_SESSION_STATUSES,
    ConnectResult,
    SessionEndReason,
    SessionRole,
    SessionStatus,
)
from app.services import diagnostics as diagnostics_service
from app.services import sessions as session_service
from app.services.app_settings import settings_service
from app.services.events import event_bus
from app.services.session_timer import scheduler
from app.ws import notify
from app.ws.protocol import (
    Candidate,
    ErrorCode,
    SessionActive,
    SessionConnected,
    SessionConnectFailed,
    SessionEnd,
    SessionEndpoint,
    SessionPeerEndpoint,
    SessionStats,
    WsError,
)

log = logging.getLogger(__name__)

MAX_DOMAINS_PER_SESSION = 200
"""Per session, after normalisation; the rest of the list is dropped."""

MAX_DOMAIN_LENGTH = 255
"""``session_domains.domain`` is ``VARCHAR(255)``."""


# ---------------------------------------------------------------- authorisation


@dataclass(frozen=True, slots=True)
class _Party:
    """The session plus which side of it the sending device is."""

    session: Session
    role: SessionRole

    @property
    def peer_device_id(self) -> uuid.UUID:
        if self.role is SessionRole.GUEST:
            return self.session.host_device_id
        return self.session.guest_device_id


async def _party(
    db: AsyncSession,
    session_id: uuid.UUID,
    device_id: uuid.UUID,
    *,
    allowed: tuple[SessionStatus, ...],
    host_only: bool = False,
) -> _Party:
    session = await db.get(Session, session_id)
    if session is None:
        raise WsError(ErrorCode.NOT_FOUND, "Unknown session")
    if device_id == session.guest_device_id:
        role = SessionRole.GUEST
    elif device_id == session.host_device_id:
        role = SessionRole.HOST
    else:
        raise WsError(ErrorCode.FORBIDDEN, "You are not a party to this session")
    if host_only and role is not SessionRole.HOST:
        # Section 5: "session.connected from the guest is ignored with error(forbidden)".
        raise WsError(ErrorCode.FORBIDDEN, "Only the host may send this")
    if session.status == SessionStatus.ENDED:
        raise WsError(ErrorCode.NOT_FOUND, "Session has already ended")
    if session.status not in allowed:
        expected = " or ".join(allowed)
        raise WsError(ErrorCode.BAD_REQUEST, f"Session is {session.status}, expected {expected}")
    return _Party(session=session, role=role)


# ---------------------------------------------------------------- endpoint exchange


async def endpoint(*, device_id: uuid.UUID, message: SessionEndpoint) -> None:
    """``session.endpoint``: store this party's material and forward it to the peer.

    Order must not matter, so the peer's stored endpoint - if it already arrived - is sent back
    to this sender in the same breath. A second frame from the same party replaces its stored
    values and is forwarded again."""
    async with session_scope() as db:
        party = await _party(db, message.session_id, device_id, allowed=(SessionStatus.CONNECTING,))
        key = await db.get(SessionKey, party.session.id)
        if key is None:  # pragma: no cover - deleted only when the session ends
            raise WsError(ErrorCode.NOT_FOUND, "Session material is gone")
        stored = [candidate.model_dump() for candidate in message.candidates]
        if party.role is SessionRole.GUEST:
            key.guest_cert_fp, key.guest_candidates = message.cert_fp_sha256, stored
            peer_fp, peer_candidates = key.host_cert_fp, key.host_candidates
        else:
            key.host_cert_fp, key.host_candidates = message.cert_fp_sha256, stored
            peer_fp, peer_candidates = key.guest_cert_fp, key.guest_candidates
        await db.commit()
        session_id, peer_device_id = party.session.id, party.peer_device_id

    await notify.send_to_device(
        peer_device_id,
        SessionPeerEndpoint(
            session_id=session_id,
            cert_fp_sha256=message.cert_fp_sha256,
            candidates=message.candidates,
        ),
    )
    if peer_fp is not None:
        await notify.send_to_device(
            device_id,
            SessionPeerEndpoint(
                session_id=session_id,
                cert_fp_sha256=peer_fp,
                candidates=[Candidate.model_validate(row) for row in peer_candidates or []],
            ),
        )


# ---------------------------------------------------------------- connecting -> active


async def connected(*, device_id: uuid.UUID, message: SessionConnected) -> None:
    """``session.connected`` from the host: the session becomes ``active`` and both parties are
    told with ``session.active``. The connect deadline is dropped, the expiry timer re-armed."""
    async with session_scope() as db:
        party = await _party(
            db,
            message.session_id,
            device_id,
            allowed=(SessionStatus.CONNECTING,),
            host_only=True,
        )
        session = party.session
        session.status = SessionStatus.ACTIVE
        session.started_at = utcnow()
        session.connect_result = ConnectResult.OK
        session.winner_type = message.winner_type
        session.connect_ms = message.connect_ms
        session.tls_version = message.tls_version
        await db.commit()
        session_id = session.id
        expires_at = ensure_utc(session.expires_at)
        assert expires_at is not None
        devices = (session.guest_device_id, session.host_device_id)

    scheduler.cancel(session_service.connect_timer_key(session_id))
    _schedule_expiry(session_id, expires_at)
    log.info(
        "session active",
        extra={
            "session_id": str(session_id),
            "winner_type": message.winner_type,
            "connect_ms": message.connect_ms,
            "tls_version": message.tls_version,
        },
    )
    frame = SessionActive(session_id=session_id, expires_at=expires_at)
    for target in devices:
        await notify.send_to_device(target, frame)


async def connect_failed(
    *, device_id: uuid.UUID, user_id: uuid.UUID, message: SessionConnectFailed
) -> None:
    """``session.connect_failed`` from either party: record the report, end with
    ``connect_failed``."""
    async with session_scope() as db:
        party = await _party(db, message.session_id, device_id, allowed=(SessionStatus.CONNECTING,))
        party.session.connect_result = ConnectResult.FAILED
        await _store_diagnostics(
            db, party, device_id=device_id, user_id=user_id, data=message.diagnostics
        )
        event = await session_service.end_session(
            db, party.session, SessionEndReason.CONNECT_FAILED
        )
        await db.commit()
    await event_bus.publish(event)


async def _store_diagnostics(
    db: AsyncSession,
    party: _Party,
    *,
    device_id: uuid.UUID,
    user_id: uuid.UUID,
    data: dict[str, Any],
) -> None:
    """One ``connect_diagnostics`` row for the reporting device. An oversized payload is dropped
    with a warning (section 8, as for ``hello.diagnostics``): it must not block the end."""
    try:
        await diagnostics_service.store(
            db,
            user_id=user_id,
            device_id=device_id,
            session_id=party.session.id,
            role=party.role.value,
            data=data,
        )
    except ValidationFailed:
        log.warning(
            "connect diagnostics dropped: payload too large",
            extra={
                "session_id": str(party.session.id),
                "bytes": diagnostics_service.data_size_bytes(data),
            },
        )


# ---------------------------------------------------------------- active


async def stats(*, device_id: uuid.UUID, message: SessionStats) -> None:
    """``session.stats`` from the host, every 30 s. Nothing is broadcast."""
    async with session_scope() as db:
        party = await _party(
            db, message.session_id, device_id, allowed=(SessionStatus.ACTIVE,), host_only=True
        )
        _apply_bytes(party.session, message.bytes_up, message.bytes_down)
        await db.commit()


def _apply_bytes(session: Session, bytes_up: int, bytes_down: int) -> None:
    """Counters only ever grow: a smaller value means the client restarted its own counting,
    not that traffic was undone."""
    session.bytes_up = max(session.bytes_up, bytes_up)
    session.bytes_down = max(session.bytes_down, bytes_down)


# ---------------------------------------------------------------- ending


async def end(*, device_id: uuid.UUID, message: SessionEnd) -> None:
    """``session.end`` from either party, from ``connecting`` or ``active``."""
    async with session_scope() as db:
        party = await _party(
            db,
            message.session_id,
            device_id,
            allowed=(SessionStatus.CONNECTING, SessionStatus.ACTIVE),
        )
        reason = _end_reason(party.role, message.reason)
        _apply_bytes(party.session, message.bytes_up, message.bytes_down)
        app_settings = await settings_service.get(db)
        if app_settings.log_domains:
            stored = await _store_domains(db, party.session.id, message.domains)
            log.info(
                "session domains recorded",
                extra={"session_id": str(party.session.id), "domains": stored},
            )
        event = await session_service.end_session(db, party.session, reason)
        await db.commit()
    await event_bus.publish(event)


def _end_reason(role: SessionRole, reason: str) -> SessionEndReason:
    """``guest_ended`` / ``host_ended`` name the sender, so they must match its side.

    ``guest_disconnected`` / ``host_disconnected`` name the *peer*: a client reports them when its
    tunnel died, so the rule is mirrored. ``browser_not_proxied`` and ``protocol_error`` are
    observations either party can make."""
    value = SessionEndReason(reason)
    mine = {
        SessionRole.GUEST: SessionEndReason.GUEST_ENDED,
        SessionRole.HOST: SessionEndReason.HOST_ENDED,
    }
    peer = {
        SessionRole.GUEST: SessionEndReason.HOST_DISCONNECTED,
        SessionRole.HOST: SessionEndReason.GUEST_DISCONNECTED,
    }
    if value in set(mine.values()) and value is not mine[role]:
        raise WsError(
            ErrorCode.BAD_REQUEST, f"reason '{reason}' does not match your role ({role.value})"
        )
    if value in set(peer.values()) and value is not peer[role]:
        raise WsError(
            ErrorCode.BAD_REQUEST,
            f"reason '{reason}' names your own side; report the peer's disconnect instead",
        )
    return value


def _normalise_domain(value: str) -> str | None:
    """Lowercase and trimmed, or ``None`` when the entry cannot be a host name."""
    name = value.strip().rstrip(".").lower()
    if not name or len(name) > MAX_DOMAIN_LENGTH:
        return None
    if any(character.isspace() or ord(character) < 0x20 for character in name):
        return None
    return name


async def _store_domains(db: AsyncSession, session_id: uuid.UUID, domains: list[str]) -> int:
    """The only place a browsed host name is ever persisted, and only with ``log_domains`` on.

    Distinct after normalisation, capped at :data:`MAX_DOMAINS_PER_SESSION`; ``hit_count`` is how
    often the name appeared in the report. Never logged, only counted."""
    counts: dict[str, int] = {}
    for raw in domains:
        name = _normalise_domain(raw)
        if name is None or (name not in counts and len(counts) >= MAX_DOMAINS_PER_SESSION):
            continue
        counts[name] = counts.get(name, 0) + 1
    if not counts:
        return 0
    known = set(
        await db.scalars(select(SessionDomain.domain).where(SessionDomain.session_id == session_id))
    )
    for name, hits in counts.items():
        if name not in known:
            db.add(SessionDomain(session_id=session_id, domain=name, hit_count=hits))
    await db.flush()
    return len(counts)


# ---------------------------------------------------------------- timers


def schedule_timers(session: Session, *, connect_timeout_seconds: int) -> None:
    """Arm both deadlines of a live session (docs/ws-protocol.md section 5).

    Called when the host accepts the request, and again for anything still live at boot. Both
    keys are idempotent - the scheduler replaces a timer scheduled twice - and both are cancelled
    by ``sessions.end_session`` whatever ends the session."""
    if session.status == SessionStatus.CONNECTING:
        created_at = ensure_utc(session.created_at) or utcnow()
        _schedule_connect_deadline(
            session.id, created_at + timedelta(seconds=connect_timeout_seconds)
        )
    expires_at = ensure_utc(session.expires_at)
    assert expires_at is not None
    _schedule_expiry(session.id, expires_at)


def _schedule_connect_deadline(session_id: uuid.UUID, deadline: datetime) -> None:
    async def fire() -> None:
        await expire_connecting(session_id)

    scheduler.schedule_at(session_service.connect_timer_key(session_id), deadline, fire)


def _schedule_expiry(session_id: uuid.UUID, expires_at: datetime) -> None:
    async def fire() -> None:
        await expire_session(session_id)

    scheduler.schedule_at(session_service.expiry_timer_key(session_id), expires_at, fire)


async def expire_connecting(session_id: uuid.UUID) -> bool:
    """Connect deadline: a session that never reported ``session.connected`` ends as
    ``connect_failed`` with ``connect_result = 'timeout'``."""
    async with session_scope() as db:
        session = await db.get(Session, session_id)
        if session is None or session.status != SessionStatus.CONNECTING:
            return False
        session.connect_result = ConnectResult.TIMEOUT
        event = await session_service.end_session(db, session, SessionEndReason.CONNECT_FAILED)
        await db.commit()
    log.info("session connect deadline", extra={"session_id": str(session_id)})
    await event_bus.publish(event)
    return True


async def expire_session(session_id: uuid.UUID) -> bool:
    """``expires_at``: end whatever is still live as ``expired``."""
    async with session_scope() as db:
        session = await db.get(Session, session_id)
        if session is None or session.status not in NON_ENDED_SESSION_STATUSES:
            return False
        event = await session_service.end_session(db, session, SessionEndReason.EXPIRED)
        await db.commit()
    log.info("session expired", extra={"session_id": str(session_id)})
    await event_bus.publish(event)
    return True


async def reschedule_timers(db: AsyncSession) -> int:
    """Boot: re-arm the timers of every session that is still live.

    The scheduler is in memory, so a session that survived a restart would otherwise never
    expire. ``sessions.end_dangling_sessions`` runs first and ends them all, so this is expected
    to find nothing; it is what keeps the two in agreement if that ever changes."""
    app_settings = await settings_service.get(db)
    rows = list(
        await db.scalars(select(Session).where(Session.status.in_(NON_ENDED_SESSION_STATUSES)))
    )
    for session in rows:
        schedule_timers(session, connect_timeout_seconds=app_settings.connect_timeout_seconds)
    if rows:
        log.warning("live sessions survived the restart", extra={"sessions": len(rows)})
    return len(rows)
