"""Device queries and revocation, shared by ``DELETE /me/devices/{id}`` and the admin API."""

import uuid

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Device
from app.models.enums import DeviceStatus, SecurityEventType
from app.services.security_events import record_event
from app.services.tokens import revoke_device_tokens


async def list_devices(
    db: AsyncSession, *, user_id: uuid.UUID | None = None, limit: int = 100
) -> list[Device]:
    stmt = select(Device).order_by(Device.created_at.desc(), Device.name).limit(limit)
    if user_id is not None:
        stmt = stmt.where(Device.user_id == user_id)
    return list(await db.scalars(stmt))


async def revoke_device(
    db: AsyncSession,
    device: Device,
    *,
    by: str,
    actor_user_id: uuid.UUID | None,
    ip: str | None,
) -> bool:
    """Mark the device revoked and revoke its refresh tokens. Idempotent: returns False (and
    writes nothing) when it was already revoked. Callers commit.

    TODO(week 3): close the device's WebSocket (4403) and clear its presence row."""
    if device.status == DeviceStatus.REVOKED:
        return False
    device.status = DeviceStatus.REVOKED
    await revoke_device_tokens(db, device.id)
    details: dict[str, str] = {"by": by}
    if actor_user_id is not None and actor_user_id != device.user_id:
        details["actor_user_id"] = str(actor_user_id)
    record_event(
        db,
        SecurityEventType.DEVICE_REVOKED,
        user_id=device.user_id,
        device_id=device.id,
        ip=ip,
        details=details,
    )
    await db.flush()
    return True
