"""Rendering domain state into frames and pushing them at connections.

Everything that fans out to clients goes through here so that ``hosts.snapshot`` (REST-shaped,
per recipient) and ``hosts.update`` (broadcast) can never disagree: both render the rows of
``app.services.hosts`` for one specific user.

``hosts.update`` is per recipient by contract (docs/ws-protocol.md section 8: the list excludes
the recipient's own devices), which made the naive fan-out cost one availability query *and* one
JSON serialisation per connected user. Week 5's load test measured that at 1.5-2.4 s of server
CPU for a single presence change with 500 connections and 200 hosts. Three things fix it without
touching the wire contract:

* the availability query runs **once** per broadcast and the exclusion is applied in memory;
* the JSON body is rendered **once** and reused for every recipient that owns no advertised
  host - which is everybody except the handful of users whose own device is in the list;
* a broadcast whose list is byte-for-byte what was last broadcast is **skipped**. Most presence
  events (a plain client connecting or disconnecting) do not change the host list at all, and
  the contract only asks for a frame "on any change".
"""

import logging
import uuid
from dataclasses import dataclass

from sqlalchemy.ext.asyncio import AsyncSession

from app.services import hosts as host_service
from app.ws.connection_manager import Connection, connection_manager
from app.ws.protocol import HostEntry, HostsSnapshot, HostsUpdate, ServerMessage

log = logging.getLogger(__name__)

type HostSignature = tuple[tuple[str, str, str, str, bool | None], ...]


@dataclass(slots=True)
class _LastBroadcast:
    """What the last ``hosts.update`` fan-out carried, so an unchanged one can be skipped.

    ``None`` means "unknown": the next broadcast always goes out. It is reset whenever the
    registry it describes is reset (server start/stop, and between tests)."""

    signature: HostSignature | None = None

    def reset(self) -> None:
        self.signature = None


_last_broadcast = _LastBroadcast()


def reset_broadcast_state() -> None:
    """Forget what was last broadcast; the next ``hosts.update`` is then unconditional."""
    _last_broadcast.reset()


def _entry(row: host_service.AvailableHost) -> HostEntry:
    return HostEntry(
        device_id=row.device_id,
        user_display_name=row.user_display_name,
        device_name=row.device_name,
        reachable=row.reachable,
    )


def _signature(rows: list[host_service.AvailableHost]) -> HostSignature:
    return tuple(
        (str(r.device_id), str(r.user_id), r.user_display_name, r.device_name, r.reachable)
        for r in rows
    )


async def send_hosts_snapshot(db: AsyncSession, connection: Connection) -> None:
    """Sent once, right after ``hello.ack`` (docs/ws-protocol.md section 4)."""
    rows = await host_service.list_available(db, exclude_user_id=connection.user_id)
    await connection.send(HostsSnapshot(hosts=[_entry(row) for row in rows]))


async def broadcast_hosts_update(db: AsyncSession) -> int:
    """Push the new full list to every connected client after a presence change.

    Returns the number of clients the frame was delivered to; ``0`` also means "nothing to say",
    because the list is exactly what everybody was last told."""
    connections = connection_manager.connections()
    if not connections:
        # Nothing was told anything, so the next broadcast must not be suppressed.
        _last_broadcast.reset()
        return 0
    rows = await host_service.list_available(db)
    signature = _signature(rows)
    if signature == _last_broadcast.signature:
        return 0
    _last_broadcast.signature = signature

    entries = [_entry(row) for row in rows]
    shared = HostsUpdate(hosts=entries).to_json()
    # Only a user with a device *in the list* needs a body of its own (its own devices are
    # filtered out); for everyone else the shared rendering is already correct.
    advertising_users = {row.user_id for row in rows}
    per_user: dict[uuid.UUID, str] = {}
    delivered = 0
    for connection in connections:
        body = shared
        if connection.user_id in advertising_users:
            body = per_user.get(connection.user_id) or _render_for(
                rows, entries, connection.user_id
            )
            per_user[connection.user_id] = body
        if await connection.send_text(body):
            delivered += 1
    return delivered


def _render_for(
    rows: list[host_service.AvailableHost], entries: list[HostEntry], user_id: uuid.UUID
) -> str:
    """The list as one specific user must see it: without that user's own devices."""
    mine = [entry for row, entry in zip(rows, entries, strict=True) if row.user_id != user_id]
    return HostsUpdate(hosts=mine).to_json()


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
