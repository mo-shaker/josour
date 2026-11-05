"""Available hosts: connected + advertising, on an active device, with no live session.

One query serves all three consumers - ``GET /hosts``, ``hosts.snapshot`` and ``hosts.update``
(docs/ws-protocol.md section 4) - so the filter can never drift between REST and WebSocket.

**Who may see a host is part of that query** (ADR-0014): a viewer sees only the hosts of their
contacts. ``visible_to`` is the single place that rule is written; ``hosts.update`` applies the
same rule in memory for a whole fan-out with ``app.services.contacts.adjacency``.
"""

import ipaddress
import uuid
from dataclasses import dataclass
from typing import Any

from sqlalchemy import or_, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.models import Contact, Device, Presence, Session, User
from app.models.enums import NON_ENDED_SESSION_STATUSES, DeviceStatus
from app.schemas.hosts import HostOut

IPV6_NETWORK_PREFIX = 64
"""An IPv6 network gives each of its devices its own address inside one /64."""


def _public_address(value: str | None) -> ipaddress.IPv4Address | ipaddress.IPv6Address | None:
    try:
        address = ipaddress.ip_address(value or "")
    except ValueError:
        return None
    if isinstance(address, ipaddress.IPv6Address) and address.ipv4_mapped is not None:
        return address.ipv4_mapped  # an IPv4 client seen through a dual-stack socket
    return address


def same_network(first: str | None, second: str | None) -> bool:
    """Two devices behind one public network, as far as their addresses tell: the same IPv4
    address (one NAT), or IPv6 addresses in the same /64. A device seen over IPv4 and another
    seen over IPv6 cannot be compared from one address each, so they count as different."""
    a, b = _public_address(first), _public_address(second)
    if a is None or b is None or a.version != b.version:
        return False
    if isinstance(a, ipaddress.IPv4Address):
        return a == b
    network = ipaddress.ip_network((a, IPV6_NETWORK_PREFIX), strict=False)
    return b in network


@dataclass(frozen=True, slots=True)
class AvailableHost:
    """One row of the availability query; ``to_out`` is the wire/REST shape."""

    device_id: uuid.UUID
    user_id: uuid.UUID
    user_display_name: str
    device_name: str
    reachable: bool | None
    public_ip: str | None = None
    """Never sent: only compared with a viewer's address (the host list's ``same_public_ip``)."""

    def same_public_ip_as(self, viewer_ip: str | None) -> bool:
        return same_network(self.public_ip, viewer_ip)

    def to_out(self, viewer_ip: str | None = None) -> HostOut:
        return HostOut(
            device_id=self.device_id,
            user_id=self.user_id,
            user_display_name=self.user_display_name,
            device_name=self.device_name,
            reachable=self.reachable,
            same_public_ip=self.same_public_ip_as(viewer_ip),
        )


def _available_query(visible_to: uuid.UUID | None) -> Any:
    """The single definition of "this host can be asked for a session right now" - and, given a
    viewer, "by this viewer". ``None`` lists every available host (the broadcast filters it per
    recipient afterwards)."""
    live_session = (
        select(Session.id)
        .where(
            Session.status.in_(NON_ENDED_SESSION_STATUSES),
            or_(Session.host_device_id == Device.id, Session.guest_device_id == Device.id),
        )
        .exists()
    )
    stmt = (
        select(
            Device.id,
            Device.user_id,
            User.display_name,
            Device.name,
            Presence.reachable,
            Presence.public_ip,
        )
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
    if visible_to is not None:
        # You never see your own devices, and you only see your contacts' (ADR-0014).
        is_contact = (
            select(Contact.user_id)
            .where(Contact.user_id == Device.user_id, Contact.contact_user_id == visible_to)
            .exists()
        )
        stmt = stmt.where(Device.user_id != visible_to, is_contact)
    return stmt


async def list_available(
    db: AsyncSession, *, visible_to: uuid.UUID | None = None
) -> list[AvailableHost]:
    stmt = _available_query(visible_to).order_by(User.display_name, Device.name)
    return [AvailableHost(*row) for row in await db.execute(stmt)]


async def list_available_hosts(
    db: AsyncSession, *, visible_to: uuid.UUID | None = None, viewer_ip: str | None = None
) -> list[HostOut]:
    """``GET /hosts``, ``hosts.snapshot`` and ``hosts.update`` all render this list."""
    return [host.to_out(viewer_ip) for host in await list_available(db, visible_to=visible_to)]


async def get_available_host(
    db: AsyncSession, device_id: uuid.UUID, *, visible_to: uuid.UUID | None = None
) -> AvailableHost | None:
    """``request.create`` target check - the same filter, narrowed to one device."""
    stmt = _available_query(visible_to).where(Device.id == device_id)
    row = (await db.execute(stmt)).first()
    return AvailableHost(*row) if row is not None else None
