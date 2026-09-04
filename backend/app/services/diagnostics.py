"""``connect_diagnostics`` rows (NAT spike results, client-reported connect failures) and the
admin summary that feeds the Relay decision gate (plan section 7.7)."""

import json
import uuid
from typing import Any

from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.errors import Forbidden, NotFound, ValidationFailed
from app.models import ConnectDiagnostic, Session
from app.models.enums import ConnectResult
from app.schemas.admin import DiagnosticsSummary

MAX_DATA_BYTES = 64 * 1024


def data_size_bytes(data: dict[str, Any]) -> int:
    return len(json.dumps(data, separators=(",", ":"), ensure_ascii=False).encode("utf-8"))


async def store(
    db: AsyncSession,
    *,
    user_id: uuid.UUID,
    device_id: uuid.UUID,
    session_id: uuid.UUID | None,
    role: str | None,
    data: dict[str, Any],
) -> ConnectDiagnostic:
    """Persist a diagnostics row for the caller's device. When ``session_id`` is given the
    session must exist and the caller must be one of its two users. Callers commit."""
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
    )
