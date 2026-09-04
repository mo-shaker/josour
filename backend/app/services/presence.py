"""The ``presence`` row is the server's view of one device's control channel.

Owned by the WebSocket layer: a row is only ever ``connected`` while a connection is registered
for that device, and every change fans out as ``hosts.update`` (docs/ws-protocol.md section 4).
Functions here flush; the callers commit (the WebSocket entry points own their transaction).
"""

import logging
import uuid
from typing import Any

from sqlalchemy import update
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.db.session import session_scope
from app.models import ConnectDiagnostic, Presence
from app.services.diagnostics import data_size_bytes
from app.services.reachability_probe import (
    PROBE_TIMEOUT_SECONDS,
    forbidden_target_reason,
    tcp_probe,
)
from app.services.session_timer import scheduler
from app.ws import notify

log = logging.getLogger(__name__)


async def get(db: AsyncSession, device_id: uuid.UUID) -> Presence | None:
    return await db.get(Presence, device_id)


async def _upsert(db: AsyncSession, device_id: uuid.UUID, **fields: Any) -> Presence:
    row = await db.get(Presence, device_id)
    if row is None:
        row = Presence(device_id=device_id)
        db.add(row)
    for key, value in fields.items():
        setattr(row, key, value)
    row.updated_at = utcnow()
    await db.flush()
    return row


async def mark_connected(
    db: AsyncSession,
    device_id: uuid.UUID,
    *,
    public_ip: str | None,
    diagnostics: dict[str, Any] | None = None,
) -> Presence:
    """``hello`` accepted: the device is online but not yet advertising itself as a host."""
    row = await _upsert(
        db,
        device_id,
        connected=True,
        is_available_host=False,
        public_ip=public_ip,
        listen_port=None,
        reachable=None,
    )
    if diagnostics:
        _store_hello_diagnostics(db, device_id, diagnostics)
    return row


def _store_hello_diagnostics(
    db: AsyncSession, device_id: uuid.UUID, diagnostics: dict[str, Any]
) -> None:
    """``hello.diagnostics`` lands in ``connect_diagnostics`` with a null ``session_id``.
    Oversized payloads are dropped, never fatal to the connection."""
    if data_size_bytes(diagnostics) > 64 * 1024:
        log.warning("dropping oversized hello diagnostics", extra={"device_id": str(device_id)})
        return
    db.add(ConnectDiagnostic(session_id=None, device_id=device_id, role=None, data=diagnostics))


async def mark_disconnected(db: AsyncSession, device_id: uuid.UUID) -> None:
    """Any disconnect: the device is offline and cannot be a host (section 1)."""
    await db.execute(
        update(Presence)
        .where(Presence.device_id == device_id)
        .values(connected=False, is_available_host=False, updated_at=utcnow())
    )


async def set_host_available(
    db: AsyncSession, device_id: uuid.UUID, *, available: bool, listen_port: int | None
) -> Presence:
    """``host.available``. Withdrawing availability also clears the stale probe result."""
    if not available:
        return await _upsert(
            db, device_id, is_available_host=False, listen_port=None, reachable=None
        )
    return await _upsert(
        db, device_id, is_available_host=True, listen_port=listen_port, reachable=None
    )


async def set_reachable(db: AsyncSession, device_id: uuid.UUID, reachable: bool | None) -> None:
    await db.execute(
        update(Presence)
        .where(Presence.device_id == device_id)
        .values(reachable=reachable, updated_at=utcnow())
    )


async def reset_all(db: AsyncSession) -> int:
    """Server boot: nobody is connected yet, so no row may claim otherwise."""
    result = await db.execute(
        update(Presence).values(
            connected=False, is_available_host=False, reachable=None, updated_at=utcnow()
        )
    )
    return result.rowcount or 0


# ---------------------------------------------------------------- reachability probe


def schedule_probe(device_id: uuid.UUID, ip: str | None, port: int) -> None:
    """Run the section 6 probe off the WebSocket handler; the handler never waits for it.

    Keyed by device, so a rapid re-advertise replaces the in-flight probe."""

    async def run() -> None:
        await probe_and_store(device_id, ip, port)

    scheduler.schedule_after(f"probe:{device_id}", 0.0, run)


async def probe_and_store(device_id: uuid.UUID, ip: str | None, port: int) -> bool | None:
    """TCP-connect to ``ip:port`` (3 s), store ``presence.reachable`` and broadcast the list.

    ``None`` (unknown) is stored when there is no usable public address - a private or loopback
    peer is never probed, exactly like ``POST /probe`` refuses one."""
    reachable: bool | None = None
    if ip is None:
        log.debug("no public ip to probe", extra={"device_id": str(device_id)})
    else:
        reason = forbidden_target_reason(ip)
        if reason is not None:
            log.debug("skipping probe", extra={"device_id": str(device_id), "reason": reason})
        else:
            result = await tcp_probe(ip, port, PROBE_TIMEOUT_SECONDS)
            reachable = result.reachable
    async with session_scope() as db:
        await set_reachable(db, device_id, reachable)
        await db.commit()
        await notify.broadcast_hosts_update(db)
    return reachable
