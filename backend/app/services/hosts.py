"""Available hosts: connected + advertising, on an active device, with no live session.

One query serves all three consumers - ``GET /hosts``, ``hosts.snapshot`` and ``hosts.update``
(docs/ws-protocol.md section 4) - so the filter can never drift between REST and WebSocket.
"""

import uuid
from dataclasses import dataclass
from typing import Any

from sqlalchemy import or_, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Device, Presence, Session, User
from app.models.enums import NON_ENDED_SESSION_STATUSES, DeviceStatus
from app.schemas.hosts import HostOut


@dataclass(frozen=True, slots=True)
class AvailableHost:
    """One row of the availability query; ``to_out`` is the wire/REST shape."""

    device_id: uuid.UUID
    user_id: uuid.UUID
    user_display_name: str
    device_name: str
    reachable: bool | None

    def to_out(self) -> HostOut:
        return HostOut(
            device_id=self.device_id,
            user_display_name=self.user_display_name,
            device_name=self.device_name,
            reachable=self.reachable,
        )


def _available_query(exclude_user_id: uuid.UUID | None) -> Any:
    """The single definition of "this host can be asked for a session right now"."""
    live_session = (
        select(Session.id)
        .where(
            Session.status.in_(NON_ENDED_SESSION_STATUSES),
            or_(Session.host_device_id == Device.id, Session.guest_device_id == Device.id),
        )
        .exists()
    )
    stmt = (
        select(Device.id, Device.user_id, User.display_name, Device.name, Presence.reachable)
        .select_from(Presence)
        .join(Device, Device.id == Presence.device_id)
        .join(User, User.id == Device.user_id)
        .where(
            Presence.connected.is_(True),
            Presence.is_available_host.is_(True),
            Device.status == DeviceStatus.ACTIVE,
            User.is_active.is_(True),
            ~live_session,
        )
    )
    if exclude_user_id is not None:
        # You never see your own devices in the host list.
        stmt = stmt.where(Device.user_id != exclude_user_id)
    return stmt


async def list_available(
    db: AsyncSession, *, exclude_user_id: uuid.UUID | None = None
) -> list[AvailableHost]:
    stmt = _available_query(exclude_user_id).order_by(User.display_name, Device.name)
    return [AvailableHost(*row) for row in await db.execute(stmt)]


async def list_available_hosts(
    db: AsyncSession, *, exclude_user_id: uuid.UUID | None = None
) -> list[HostOut]:
    """``GET /hosts``, ``hosts.snapshot`` and ``hosts.update`` all render this list."""
    return [host.to_out() for host in await list_available(db, exclude_user_id=exclude_user_id)]


async def get_available_host(
    db: AsyncSession, device_id: uuid.UUID, *, exclude_user_id: uuid.UUID | None = None
) -> AvailableHost | None:
    """``request.create`` target check - the same filter, narrowed to one device."""
    stmt = _available_query(exclude_user_id).where(Device.id == device_id)
    row = (await db.execute(stmt)).first()
    return AvailableHost(*row) if row is not None else None
