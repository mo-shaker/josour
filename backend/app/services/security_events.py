"""Audit trail rows (security_events). Never store secrets in ``details``."""

import uuid
from typing import Any

from sqlalchemy.ext.asyncio import AsyncSession

from app.models import SecurityEvent
from app.models.enums import SecurityEventType


def record_event(
    db: AsyncSession,
    event_type: SecurityEventType,
    *,
    user_id: uuid.UUID | None = None,
    device_id: uuid.UUID | None = None,
    ip: str | None = None,
    details: dict[str, Any] | None = None,
) -> SecurityEvent:
    event = SecurityEvent(
        type=event_type, user_id=user_id, device_id=device_id, ip=ip, details=details
    )
    db.add(event)
    return event
