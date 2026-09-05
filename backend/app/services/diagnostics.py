"""``connect_diagnostics`` rows (NAT spike results, client-reported connect failures) and the
admin summary that feeds the Relay decision gate (plan section 7.7)."""

import ipaddress
import json
import uuid
from typing import Any

from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.errors import Forbidden, NotFound, ValidationFailed
from app.models import ConnectDiagnostic, Session
from app.models.enums import ConnectResult, SecurityEventType
from app.schemas.admin import DiagnosticsSummary
from app.services.security_events import record_event

MAX_DATA_BYTES = 64 * 1024

LISTENER_UNAUTHENTICATED_KEY = "listener_unauthenticated"
"""Reserved key inside ``data`` (docs/api.md, ``POST /diagnostics``)."""

MAX_REPORTED_PEERS = 10
MAX_REPORTED_COUNT = 10_000
"""The count is clamped: it is a client-supplied number and it feeds nothing but an audit row."""


def data_size_bytes(data: dict[str, Any]) -> int:
    return len(json.dumps(data, separators=(",", ":"), ensure_ascii=False).encode("utf-8"))


def _positive_count(value: Any) -> int | None:
    """``True`` is an ``int`` in Python and ``data`` is free-form, so booleans are excluded
    explicitly: the contract asks for a *numeric* count."""
    if isinstance(value, bool) or not isinstance(value, int | float):
        return None
    if not value > 0:
        return None
    return min(int(value), MAX_REPORTED_COUNT)


def _peers(value: Any) -> list[str]:
    """At most ten source addresses, canonicalised. Anything that is not an IP address is
    dropped rather than stored: this signal must never become a channel for domain names or
    payloads (privacy items 15.12 and 14.20)."""
    if not isinstance(value, list):
        return []
    seen: list[str] = []
    for item in value:
        if len(seen) >= MAX_REPORTED_PEERS:
            break
        if not isinstance(item, str) or len(item) > 45:
            continue
        try:
            address = str(ipaddress.ip_address(item.strip()))
        except ValueError:
            continue
        if address not in seen:
            seen.append(address)
    return seen


def listener_signal(data: dict[str, Any]) -> dict[str, Any] | None:
    """The ``listener_unauthenticated`` security signal carried inside a diagnostics ``data``
    object, or ``None`` when the object does not carry one.

    Returns only what may be persisted: a clamped count, an optional port, and up to ten source
    IP addresses. Every other key of ``data`` is ignored here - it stays in the diagnostics row
    and never reaches ``security_events``.
    """
    count = _positive_count(data.get(LISTENER_UNAUTHENTICATED_KEY))
    if count is None:
        return None
    details: dict[str, Any] = {"count": count}
    port = data.get("listener_port")
    if not isinstance(port, bool) and isinstance(port, int) and 1 <= port <= 65535:
        details["listener_port"] = port
    peers = _peers(data.get("unauthenticated_peers"))
    if peers:
        details["peers"] = peers
    return details


async def store(
    db: AsyncSession,
    *,
    user_id: uuid.UUID,
    device_id: uuid.UUID,
    session_id: uuid.UUID | None,
    role: str | None,
    data: dict[str, Any],
    ip: str | None = None,
) -> ConnectDiagnostic:
    """Persist a diagnostics row for the caller's device. When ``session_id`` is given the
    session must exist and the caller must be one of its two users. Callers commit.

    A ``data`` object carrying the reserved ``listener_unauthenticated`` key also writes a
    ``security_events`` row of that type (docs/api.md). The ingestion lives here rather than in
    the router so that no path can store such a payload without raising the signal.
    """
    if data_size_bytes(data) > MAX_DATA_BYTES:
        raise ValidationFailed(f"data must not exceed {MAX_DATA_BYTES // 1024} KB")
    if session_id is not None:
        session = await db.get(Session, session_id)
        if session is None:
            raise NotFound("Session not found")
        if user_id not in (session.guest_user_id, session.host_user_id):
            raise Forbidden("Not a participant of this session")
    row = ConnectDiagnostic(session_id=session_id, device_id=device_id, role=role, data=data)
    db.add(row)
    signal = listener_signal(data)
    if signal is not None:
        record_event(
            db,
            SecurityEventType.LISTENER_UNAUTHENTICATED,
            user_id=user_id,
            device_id=device_id,
            ip=ip,
            details=signal,
        )
    await db.flush()
    return row


async def _distribution(db: AsyncSession, column: Any) -> dict[str, int]:
    rows = await db.execute(
        select(column, func.count()).where(column.is_not(None)).group_by(column).order_by(column)
    )
    return {str(value): int(count) for value, count in rows}


async def summarize(db: AsyncSession) -> DiagnosticsSummary:
    total = await db.scalar(select(func.count()).select_from(Session)) or 0
    with_result = (
        await db.scalar(
            select(func.count()).select_from(Session).where(Session.connect_result.is_not(None))
        )
        or 0
    )
    ok = (
        await db.scalar(
            select(func.count())
            .select_from(Session)
            .where(Session.connect_result == ConnectResult.OK)
        )
        or 0
    )
    diagnostics = await db.scalar(select(func.count()).select_from(ConnectDiagnostic)) or 0
    return DiagnosticsSummary(
        total_sessions=int(total),
        sessions_with_connect_result=int(with_result),
        connect_ok_ratio=(ok / with_result) if with_result else 0.0,
        winner_type_distribution=await _distribution(db, Session.winner_type),
        tls_version_distribution=await _distribution(db, Session.tls_version),
        connect_diagnostics_count=int(diagnostics),
        end_reason_distribution=await _distribution(db, Session.end_reason),
    )
