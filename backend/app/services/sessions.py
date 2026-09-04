"""Session queries (``GET /sessions/me``, ``GET /admin/sessions``) and the admin termination.

Week 3/4 adds the full state machine (request accept -> connecting -> active, timers,
disconnects) on top of this module. ``end_session`` is the single place that performs the
``ended`` transition and deletes the ``session_keys`` row, so every end reason must go through
it; the returned ``SessionEnded`` event is published on the bus by the caller after commit.
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
from app.models.enums import SecurityEventType, SessionEndReason, SessionRole, SessionStatus
from app.schemas.sessions import AdminSessionOut, MySessionOut
from app.services.events import SessionEnded
from app.services.security_events import record_event

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


async def end_session(
    db: AsyncSession,
    session: Session,
    reason: SessionEndReason,
    *,
    now: datetime | None = None,
) -> SessionEnded:
    """Transition to ``ended`` and delete the ``session_keys`` row (docs/ws-protocol.md
    section 5). Flushes only; the caller commits and then publishes the returned event."""
    now = now or utcnow()
    session.status = SessionStatus.ENDED
    session.ended_at = now
    session.end_reason = reason
    await db.execute(delete(SessionKey).where(SessionKey.session_id == session.id))
    await db.flush()
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


async def admin_terminate(
    db: AsyncSession,
    session_id: uuid.UUID,
    *,
    admin_user_id: uuid.UUID,
    admin_device_id: uuid.UUID | None,
    ip: str | None,
) -> SessionEnded:
    """``POST /admin/sessions/{id}/terminate``: 404 unknown, 409 already ended."""
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
