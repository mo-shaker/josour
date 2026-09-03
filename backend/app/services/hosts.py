"""Available hosts: connected + advertising, on an active device, with no live session."""

from sqlalchemy import or_, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Device, Presence, Session, User
from app.models.enums import NON_ENDED_SESSION_STATUSES, DeviceStatus
from app.schemas.hosts import HostOut


async def list_available_hosts(db: AsyncSession) -> list[HostOut]:
    """Shared by ``GET /hosts`` and (TODO week 3) ``hosts.snapshot`` / ``hosts.update``."""
    live_session = (
        select(Session.id)
        .where(
            Session.status.in_(NON_ENDED_SESSION_STATUSES),
            or_(Session.host_device_id == Device.id, Session.guest_device_id == Device.id),
        )
        .exists()
    )
    stmt = (
        select(Device.id, User.display_name, Device.name, Presence.reachable)
        .join(Device, Device.id == Presence.device_id)
        .join(User, User.id == Device.user_id)
        .where(
            Presence.connected.is_(True),
            Presence.is_available_host.is_(True),
            Device.status == DeviceStatus.ACTIVE,
            User.is_active.is_(True),
            ~live_session,
        )
        .order_by(User.display_name, Device.name)
    )
    rows = await db.execute(stmt)
    return [
        HostOut(device_id=device_id, user_display_name=user_name, device_name=name, reachable=r)
        for device_id, user_name, name, r in rows
    ]
