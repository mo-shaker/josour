"""Rendering domain state into frames and pushing them at connections.

Everything that fans out to clients goes through here so that ``hosts.snapshot`` (REST-shaped,
per recipient) and ``hosts.update`` (broadcast) can never disagree: both render the rows of
``app.services.hosts`` as one specific user may see them.

``hosts.update`` is per recipient by contract: the list holds only the recipient's **contacts'**
hosts (ADR-0014) and never the recipient's own devices. The naive fan-out would cost one
availability query and one JSON serialisation per connected user; week 5's load test measured
that at 1.5-2.4 s of server CPU for a single presence change with 500 connections and 200 hosts.
So:

* the availability query runs **once** per broadcast, and the contact links of everybody
  connected are read in **one** more query (``contacts.adjacency``); the filtering is in memory;
* each distinct visible list is rendered to JSON **once** and the string reused for every
  recipient who sees exactly that list - with contacts, most people share few lists;
* a recipient whose list is exactly what they were last told is **skipped**. Most presence events
  (a plain client connecting or disconnecting) change nobody's list, and the contract only asks
  for a frame "on any change". What each connection was last told lives on the connection.
"""

import logging
import uuid

from sqlalchemy.ext.asyncio import AsyncSession

from app.services import contacts as contact_service
from app.services import hosts as host_service
from app.ws.connection_manager import Connection, connection_manager
from app.ws.protocol import (
    Capability,
    ContactPresenceEntry,
    ContactsPresence,
    HostEntry,
    HostsSnapshot,
    HostState,
    HostsUpdate,
    ServerMessage,
)

log = logging.getLogger(__name__)

type HostSignature = tuple[tuple[str, str, str, str, bool | None], ...]


def reset_broadcast_state() -> None:
    """Forget what every connection was last told; the next ``hosts.update`` then reaches all.

    Kept for the lifecycle and the tests; the state itself now lives on each connection."""
    for connection in connection_manager.connections():
        connection.last_hosts_signature = None


def _entry(row: host_service.AvailableHost) -> HostEntry:
    return HostEntry(
        device_id=row.device_id,
        user_id=row.user_id,
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
    rows = await host_service.list_available(db, visible_to=connection.user_id)
    await connection.send(HostsSnapshot(hosts=[_entry(row) for row in rows]))
    connection.last_hosts_signature = _signature(rows)


async def broadcast_hosts_update(db: AsyncSession) -> int:
    """Push each connected client its new list after a presence or contact change.

    Returns the number of clients a frame was delivered to; ``0`` also means "nothing to say",
    because every list is exactly what its recipient was last told."""
    connections = connection_manager.connections()
    if not connections:
        return 0
    rows = await host_service.list_available(db)
    links = await contact_service.adjacency(
        db, (c.user_id for c in connections), (r.user_id for r in rows)
    )

    rendered: dict[HostSignature, str] = {}
    delivered = 0
    for connection in connections:
        visible_owners = links.get(connection.user_id, set())
        mine = [r for r in rows if r.user_id in visible_owners and r.user_id != connection.user_id]
        signature = _signature(mine)
        if signature == connection.last_hosts_signature:
            continue
        body = rendered.get(signature)
        if body is None:
            body = HostsUpdate(hosts=[_entry(r) for r in mine]).to_json()
            rendered[signature] = body
        if await connection.send_text(body):
            connection.last_hosts_signature = signature
            delivered += 1
    return delivered


async def send_contacts_presence(db: AsyncSession, connection: Connection) -> None:
    """The full ``contacts.presence`` after ``hello``, for clients that understand it."""
    ids = await contact_service.contact_ids(db, connection.user_id)
    await connection.send(
        ContactsPresence(
            full=True,
            contacts=[
                ContactPresenceEntry(user_id=uid, online=connection_manager.is_user_online(uid))
                for uid in ids
            ],
        )
    )


async def announce_presence(db: AsyncSession, user_id: uuid.UUID, *, online: bool) -> int:
    """A user's first device connected or last device left: tell their contacts' devices."""
    ids = set(await contact_service.contact_ids(db, user_id))
    if not ids:
        return 0
    message = ContactsPresence(
        full=False, contacts=[ContactPresenceEntry(user_id=user_id, online=online)]
    )
    delivered = 0
    for connection in connection_manager.connections():
        if connection.user_id in ids and connection.can(Capability.CONTACTS):
            if await connection.send(message):
                delivered += 1
    return delivered


async def send_host_state(device_id: uuid.UUID, *, available: bool, reachable: bool | None) -> bool:
    """``host.state`` to the device itself, if it understands it."""
    connection = connection_manager.get(device_id)
    if connection is None or not connection.can(Capability.CONTACTS):
        return False
    return await connection.send(HostState(available=available, reachable=reachable))


async def send_to_device(device_id: uuid.UUID, message: ServerMessage) -> bool:
    """``False`` when the device is not connected; callers decide whether that matters."""
    return await connection_manager.send(device_id, message)


async def send_to_user(
    user_id: uuid.UUID, message: ServerMessage, *, capability: str | None = None
) -> int:
    """Every connected device of a user (optionally only those declaring ``capability``)."""
    delivered = 0
    for connection in connection_manager.for_user(user_id):
        if capability is not None and not connection.can(capability):
            continue
        if await connection.send(message):
            delivered += 1
    return delivered


async def close_device(device_id: uuid.UUID, code: int) -> bool:
    """Drop a device's control channel (device revocation -> 4403)."""
    connection = connection_manager.get(device_id)
    if connection is None:
        return False
    await connection.close(code)
    return True
