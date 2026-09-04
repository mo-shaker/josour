"""Rendering domain state into frames and pushing them at connections.

Everything that fans out to clients goes through here so that ``hosts.snapshot`` (REST-shaped,
per recipient) and ``hosts.update`` (broadcast) can never disagree: both render the rows of
``app.services.hosts`` for one specific user.
"""

import logging
import uuid

from sqlalchemy.ext.asyncio import AsyncSession

from app.services import hosts as host_service
from app.ws.connection_manager import Connection, connection_manager
from app.ws.protocol import HostEntry, HostsSnapshot, HostsUpdate, ServerMessage

log = logging.getLogger(__name__)


async def _entries(db: AsyncSession, user_id: uuid.UUID) -> list[HostEntry]:
    rows = await host_service.list_available(db, exclude_user_id=user_id)
    return [
        HostEntry(
            device_id=row.device_id,
            user_display_name=row.user_display_name,
            device_name=row.device_name,
            reachable=row.reachable,
        )
        for row in rows
    ]


async def send_hosts_snapshot(db: AsyncSession, connection: Connection) -> None:
    """Sent once, right after ``hello.ack`` (docs/ws-protocol.md section 4)."""
    await connection.send(HostsSnapshot(hosts=await _entries(db, connection.user_id)))


async def broadcast_hosts_update(db: AsyncSession) -> int:
    """Push the new full list to every connected client after any presence change.

    The list is per recipient (own devices are filtered out), so it is computed once per user
    and reused for that user's connections."""
    connections = connection_manager.connections()
    if not connections:
        return 0
    by_user: dict[uuid.UUID, list[HostEntry]] = {}
    delivered = 0
    for connection in connections:
        if connection.user_id not in by_user:
            by_user[connection.user_id] = await _entries(db, connection.user_id)
        if await connection.send(HostsUpdate(hosts=by_user[connection.user_id])):
            delivered += 1
    return delivered


async def send_to_device(device_id: uuid.UUID, message: ServerMessage) -> bool:
    """``False`` when the device is not connected; callers decide whether that matters."""
    return await connection_manager.send(device_id, message)


async def close_device(device_id: uuid.UUID, code: int) -> bool:
    """Drop a device's control channel (device revocation -> 4403)."""
    connection = connection_manager.get(device_id)
    if connection is None:
        return False
    await connection.close(code)
    return True
