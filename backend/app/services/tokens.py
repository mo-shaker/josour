"""Refresh-token lifecycle: issue, rotate (with reuse detection), revoke."""

import uuid
from datetime import timedelta

from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.config import Settings
from app.core.errors import ApiError, Unauthorized
from app.core.security import generate_opaque_secret, hash_opaque_secret
from app.models import Device, RefreshToken, User
from app.models.enums import DeviceStatus, SecurityEventType
from app.services.security_events import record_event


async def issue_refresh_token(
    db: AsyncSession, settings: Settings, user_id: uuid.UUID, device_id: uuid.UUID
) -> str:
    """Insert a new row and return the plaintext token (never stored, never logged)."""
    token = generate_opaque_secret()
    db.add(
        RefreshToken(
            user_id=user_id,
            device_id=device_id,
            token_hash=hash_opaque_secret(token),
            expires_at=utcnow() + timedelta(days=settings.refresh_token_days),
        )
    )
    await db.flush()
    return token


async def find_refresh_token(db: AsyncSession, presented: str) -> RefreshToken | None:
    return await db.scalar(
        select(RefreshToken).where(RefreshToken.token_hash == hash_opaque_secret(presented))
    )


async def revoke_device_tokens(db: AsyncSession, device_id: uuid.UUID) -> int:
    result = await db.execute(
        update(RefreshToken)
        .where(RefreshToken.device_id == device_id, RefreshToken.revoked_at.is_(None))
        .values(revoked_at=utcnow())
    )
    return result.rowcount or 0


async def revoke_user_tokens(db: AsyncSession, user_id: uuid.UUID) -> int:
    """Revoke every live refresh token of a user (admin deactivation / password reset)."""
    result = await db.execute(
        update(RefreshToken)
        .where(RefreshToken.user_id == user_id, RefreshToken.revoked_at.is_(None))
        .values(revoked_at=utcnow())
    )
    return result.rowcount or 0


async def rotate_refresh_token(
    db: AsyncSession, settings: Settings, presented: str, ip: str | None
) -> tuple[str, User, Device]:
    """Validate ``presented``, revoke it and issue a replacement.

    Reuse detection: a token that was already rotated/revoked revokes every live token of the
    same device (the whole chain) and the request fails. Commits on every outcome."""
    row = await find_refresh_token(db, presented)
    if row is None:
        raise Unauthorized("Invalid refresh token")
    now = utcnow()
    if row.revoked_at is not None:
        revoked = await revoke_device_tokens(db, row.device_id)
        record_event(
            db,
            SecurityEventType.REFRESH_REUSE,
            user_id=row.user_id,
            device_id=row.device_id,
            ip=ip,
            details={"revoked_tokens": revoked},
        )
        await db.commit()
        raise Unauthorized("Refresh token reuse detected; session revoked")
    expires_at = ensure_utc(row.expires_at)
    if expires_at is None or expires_at <= now:
        row.revoked_at = now
        await db.commit()
        raise Unauthorized("Refresh token expired")

    user = await db.get(User, row.user_id)
    device = await db.get(Device, row.device_id)
    if user is None or device is None:
        raise Unauthorized("Invalid refresh token")
    if not user.is_active:
        raise ApiError(403, "account_disabled", "Account is disabled")
    if device.status == DeviceStatus.REVOKED:
        raise ApiError(403, "device_revoked", "Device has been revoked")

    row.revoked_at = now
    new_token = await issue_refresh_token(db, settings, user.id, device.id)
    device.last_seen_at = now
    await db.commit()
    return new_token, user, device


async def revoke_refresh_token(db: AsyncSession, presented: str, ip: str | None) -> bool:
    """Logout: revoke the presented token if it is live. Commits. Returns whether a row changed."""
    row = await find_refresh_token(db, presented)
    if row is None or row.revoked_at is not None:
        return False
    row.revoked_at = utcnow()
    record_event(db, SecurityEventType.LOGOUT, user_id=row.user_id, device_id=row.device_id, ip=ip)
    await db.commit()
    return True
