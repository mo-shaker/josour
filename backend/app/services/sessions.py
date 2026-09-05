"""Session queries (``GET /sessions/me``, ``GET /admin/sessions``) and the admin termination.

The frame-driven half of the state machine (endpoint exchange, ``connecting`` -> ``active``,
stats, client-requested ends) lives in ``app.services.session_flow``; this module owns the
``ended`` transition itself.

``end_session`` is the single place that performs it, so every reason of docs/ws-protocol.md
section 5 - client ``session.end``, control-channel disconnect, admin termination, the connect
deadline, the ``expires_at`` timer and the boot-time sweep - converges on the same four effects:
the row is marked ended, its ``session_keys`` secret is deleted, its two timers are cancelled,
and the returned ``SessionEnded`` event is published on the bus by the caller after commit
(which is what delivers ``session.terminate`` to the peers).
"""

import uuid
from collections.abc import Sequence
from datetime import datetime
from typing import Any

from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.orm import aliased

from app.core.clock import ensure_utc, utcnow
from app.core.errors import Conflict, NotFound
from app.models import Device, SecurityEvent, Session, SessionKey, User
from app.models.enums import (
    NON_ENDED_SESSION_STATUSES,
    SecurityEventType,
    SessionEndReason,
    SessionRole,
    SessionStatus,
)
from app.schemas.sessions import AdminSessionOut, MySessionOut
from app.services.events import SessionEnded
from app.services.security_events import record_event
from app.services.session_timer import scheduler

GuestUser = aliased(User, name="guest_user")
GuestDevice = aliased(Device, name="guest_device")
HostUser = aliased(User, name="host_user")
HostDevice = aliased(Device, name="host_device")

# (Session, guest display name, guest device name, host display name, host device name)
type SessionRow = tuple[Session, str, str, str, str]


def _rows_query() -> Any:
    return (
        select(
            Session,
            GuestUser.display_name,
            GuestDevice.name,
            HostUser.display_name,
            HostDevice.name,
        )
        .join(GuestUser, GuestUser.id == Session.guest_user_id)
        .join(GuestDevice, GuestDevice.id == Session.guest_device_id)
        .join(HostUser, HostUser.id == Session.host_user_id)
        .join(HostDevice, HostDevice.id == Session.host_device_id)
        .order_by(Session.created_at.desc(), Session.id)
    )


async def list_for_user(db: AsyncSession, user_id: uuid.UUID, *, limit: int) -> list[MySessionOut]:
    """Sessions where the user was guest or host, newest first, seen from the user's side."""
    stmt = (
        _rows_query()
        .where((Session.guest_user_id == user_id) | (Session.host_user_id == user_id))
        .limit(limit)
    )
    rows: Sequence[SessionRow] = (await db.execute(stmt)).tuples().all()  # type: ignore[assignment]
    out: list[MySessionOut] = []
    for session, guest_name, guest_device, host_name, host_device in rows:
        is_guest = session.guest_user_id == user_id
        out.append(
            MySessionOut(
                id=session.id,
                role=SessionRole.GUEST if is_guest else SessionRole.HOST,
                peer_display_name=host_name if is_guest else guest_name,
                peer_device_name=host_device if is_guest else guest_device,
                status=session.status,
                created_at=session.created_at,
                started_at=session.started_at,
                ended_at=session.ended_at,
                end_reason=session.end_reason,
                bytes_up=session.bytes_up,
                bytes_down=session.bytes_down,
            )
        )
    return out


async def list_all(
    db: AsyncSession, *, status: SessionStatus | None = None, limit: int = 100
) -> list[AdminSessionOut]:
    stmt = _rows_query().limit(limit)
    if status is not None:
        stmt = stmt.where(Session.status == status)
    rows: Sequence[SessionRow] = (await db.execute(stmt)).tuples().all()  # type: ignore[assignment]
    return [
        AdminSessionOut(
            id=s.id,
            status=s.status,
            guest_user_id=s.guest_user_id,
            guest_display_name=guest_name,
            guest_device_id=s.guest_device_id,
            guest_device_name=guest_device,
            host_user_id=s.host_user_id,
            host_display_name=host_name,
            host_device_id=s.host_device_id,
            host_device_name=host_device,
            created_at=s.created_at,
            started_at=s.started_at,
            expires_at=s.expires_at,
            ended_at=s.ended_at,
            end_reason=s.end_reason,
            bytes_up=s.bytes_up,
            bytes_down=s.bytes_down,
            connect_result=s.connect_result,
            winner_type=s.winner_type,
            tls_version=s.tls_version,
            connect_ms=s.connect_ms,
        )
        for s, guest_name, guest_device, host_name, host_device in rows
    ]


def connect_timer_key(session_id: uuid.UUID) -> str:
    """The connect deadline of docs/ws-protocol.md section 5 (30 s from ``session.created``)."""
    return f"session-connect:{session_id}"


def expiry_timer_key(session_id: uuid.UUID) -> str:
    """The session's ``expires_at`` timer."""
    return f"session-expiry:{session_id}"


def cancel_session_timers(session_id: uuid.UUID) -> int:
    """Drop both deadlines of a session. Called from :func:`end_session`, so no path can end a
    session and leave a timer behind that would later fire on an ended row."""
    keys = (connect_timer_key(session_id), expiry_timer_key(session_id))
    return sum(scheduler.cancel(key) for key in keys)


async def end_session(
    db: AsyncSession,
    session: Session,
    reason: SessionEndReason,
    *,
    now: datetime | None = None,
) -> SessionEnded:
    """Transition to ``ended``, delete the ``session_keys`` row and cancel the session's timers
    (docs/ws-protocol.md section 5). Flushes only; the caller commits and then publishes the
    returned event, which is what sends ``session.terminate`` to the peers."""
    now = now or utcnow()
    session.status = SessionStatus.ENDED
    session.ended_at = now
    session.end_reason = reason
    await db.execute(delete(SessionKey).where(SessionKey.session_id == session.id))
    await db.flush()
    cancel_session_timers(session.id)
    ended_at = ensure_utc(now)
    assert ended_at is not None
    return SessionEnded(
        session_id=session.id,
        guest_user_id=session.guest_user_id,
        guest_device_id=session.guest_device_id,
        host_user_id=session.host_user_id,
        host_device_id=session.host_device_id,
        reason=reason,
        ended_at=ended_at,
    )


async def get_live_for_device(db: AsyncSession, device_id: uuid.UUID) -> Session | None:
    """The device's non-ended session, if any (at most one; partial unique indexes)."""
    return await db.scalar(
        select(Session).where(
            Session.status.in_(NON_ENDED_SESSION_STATUSES),
            (Session.guest_device_id == device_id) | (Session.host_device_id == device_id),
        )
    )


async def get_live_for_user(db: AsyncSession, user_id: uuid.UUID) -> Session | None:
    return await db.scalar(
        select(Session).where(
            Session.status.in_(NON_ENDED_SESSION_STATUSES),
            (Session.guest_user_id == user_id) | (Session.host_user_id == user_id),
        )
    )


async def end_device_sessions(db: AsyncSession, device_id: uuid.UUID) -> list[SessionEnded]:
    """The device's control channel dropped: end its session as ``guest_disconnected`` or
    ``host_disconnected`` depending on which side it was (docs/ws-protocol.md section 1)."""
    rows = await db.scalars(
        select(Session).where(
            Session.status.in_(NON_ENDED_SESSION_STATUSES),
            (Session.guest_device_id == device_id) | (Session.host_device_id == device_id),
        )
    )
    events = []
    for session in rows:
        reason = (
            SessionEndReason.GUEST_DISCONNECTED
            if session.guest_device_id == device_id
            else SessionEndReason.HOST_DISCONNECTED
        )
        events.append(await end_session(db, session, reason))
    return events


async def end_dangling_sessions(db: AsyncSession) -> list[SessionEnded]:
    """Server boot: no control channel survived the restart, so nothing can still be live.

    Invariant with the scheduler: the timers of section 5 live in memory only, so a session that
    outlived a restart would have none. This sweep is what guarantees there is no such session -
    it ends **every** non-ended row - and ``session_flow.reschedule_timers`` re-arms whatever it
    left behind, so the two always agree on which sessions are live and timed. Today the second
    call is a no-op by construction; it exists so that relaxing this sweep cannot silently
    produce a live session that nothing will ever expire."""
    rows = await db.scalars(select(Session).where(Session.status.in_(NON_ENDED_SESSION_STATUSES)))
    return [await end_session(db, session, SessionEndReason.HOST_DISCONNECTED) for session in rows]


async def admin_terminate(
    db: AsyncSession,
    session_id: uuid.UUID,
    *,
    admin_user_id: uuid.UUID | None,
    admin_device_id: uuid.UUID | None,
    ip: str | None,
) -> SessionEnded:
    """Central session termination: ``POST /admin/sessions/{id}/terminate`` and the
    ``end-session`` CLI command. 404 unknown, 409 already ended.

    ``admin_user_id`` is ``None`` for the CLI, which runs on the server host with database
    credentials rather than as a logged-in user; the audit row then records ``via`` instead."""
    session = await db.get(Session, session_id)
    if session is None:
        raise NotFound("Session not found")
    if session.status == SessionStatus.ENDED:
        raise Conflict("Session already ended")
    event = await end_session(db, session, SessionEndReason.ADMIN_TERMINATED)
    record_event(
        db,
        SecurityEventType.SESSION_ADMIN_TERMINATED,
        user_id=admin_user_id,
        device_id=admin_device_id,
        ip=ip,
        details={
            "via": "api" if admin_user_id is not None else "cli",
            "session_id": str(session.id),
            "guest_user_id": str(session.guest_user_id),
            "host_user_id": str(session.host_user_id),
        },
    )
    return event


async def list_security_events(
    db: AsyncSession, *, event_type: str | None = None, limit: int = 100
) -> list[SecurityEvent]:
    stmt = select(SecurityEvent).order_by(SecurityEvent.created_at.desc(), SecurityEvent.id)
    if event_type:
        stmt = stmt.where(SecurityEvent.type == event_type)
    return list(await db.scalars(stmt.limit(limit)))
