"""Server-side sessions of the web dashboard (ADR-0016). Callers commit.

The browser holds an opaque token in a ``__Host-`` cookie; the row holds its SHA-256, the CSRF
token of the forms, and whether the second factor has been passed. Keeping the session on the
server is what lets it be revoked: a password change, deactivation, demotion or deletion ends
every dashboard session of that administrator.
"""

import secrets
import uuid
from dataclasses import dataclass
from datetime import timedelta

from sqlalchemy import delete, or_, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.security import generate_opaque_secret, hash_opaque_secret
from app.models import AdminWebSession, User
from app.models.enums import UserRole

TOUCH_EVERY = timedelta(minutes=1)
"""``last_seen_at`` is written at most this often, not on every page."""


@dataclass(frozen=True, slots=True)
class Issued:
    row: AdminWebSession
    token: str
    """The cookie value. Returned once; only its hash is stored."""


async def create(
    db: AsyncSession,
    user: User,
    *,
    mfa_passed: bool,
    ip: str | None,
    user_agent: str | None,
    absolute_hours: int,
) -> Issued:
    token = generate_opaque_secret()
    now = utcnow()
    row = AdminWebSession(
        token_hash=hash_opaque_secret(token),
        user_id=user.id,
        csrf_token=secrets.token_urlsafe(32),
        mfa_passed=mfa_passed,
        ip=ip,
        user_agent=(user_agent or "")[:255] or None,
        created_at=now,
        last_seen_at=now,
        expires_at=now + timedelta(hours=absolute_hours),
    )
    db.add(row)
    await db.flush()
    return Issued(row=row, token=token)


async def rotate(db: AsyncSession, row: AdminWebSession) -> str:
    """A new token and CSRF token for the same session (after the second factor), so a token
    seen before the privilege changed is worth nothing afterwards."""
    token = generate_opaque_secret()
    row.token_hash = hash_opaque_secret(token)
    row.csrf_token = secrets.token_urlsafe(32)
    await db.flush()
    return token


async def resolve(
    db: AsyncSession, token: str | None, *, idle_minutes: int
) -> tuple[AdminWebSession, User] | None:
    """The live session for a cookie value, or ``None`` (and the stale row is revoked)."""
    if not token or len(token) > 128:
        return None
    row = await db.scalar(
        select(AdminWebSession).where(AdminWebSession.token_hash == hash_opaque_secret(token))
    )
    if row is None or row.revoked_at is not None:
        return None
    now = utcnow()
    expires_at = ensure_utc(row.expires_at)
    last_seen = ensure_utc(row.last_seen_at)
    assert expires_at is not None and last_seen is not None
    if expires_at <= now or last_seen + timedelta(minutes=idle_minutes) <= now:
        row.revoked_at = now
        await db.flush()
        return None
    user = await db.get(User, row.user_id)
    if (
        user is None
        or not user.is_active
        or user.deleted_at is not None
        or user.role != UserRole.ADMIN
    ):
        row.revoked_at = now
        await db.flush()
        return None
    if now - last_seen >= TOUCH_EVERY:
        row.last_seen_at = now
        await db.flush()
    return row, user


async def revoke(db: AsyncSession, row: AdminWebSession) -> None:
    row.revoked_at = utcnow()
    await db.flush()


async def revoke_all_for_user(db: AsyncSession, user_id: uuid.UUID) -> int:
    result = await db.execute(
        update(AdminWebSession)
        .where(AdminWebSession.user_id == user_id, AdminWebSession.revoked_at.is_(None))
        .values(revoked_at=utcnow())
    )
    return result.rowcount or 0


async def purge(db: AsyncSession, *, keep_days: int = 30) -> int:
    """Drop rows long expired or revoked (the hourly housekeeping job)."""
    cutoff = utcnow() - timedelta(days=keep_days)
    result = await db.execute(
        delete(AdminWebSession).where(
            or_(AdminWebSession.expires_at < cutoff, AdminWebSession.revoked_at < cutoff)
        )
    )
    return result.rowcount or 0
