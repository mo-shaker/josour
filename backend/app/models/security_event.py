import uuid
from datetime import datetime
from typing import Any

from sqlalchemy import ForeignKey, Index, String, Uuid
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import JSONVariant, UtcDateTime


class SecurityEvent(Base):
    __tablename__ = "security_events"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    type: Mapped[str] = mapped_column(String(64), nullable=False)
    user_id: Mapped[uuid.UUID | None] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="SET NULL"), nullable=True
    )
    device_id: Mapped[uuid.UUID | None] = mapped_column(
        Uuid, ForeignKey("devices.id", ondelete="SET NULL"), nullable=True
    )
    ip: Mapped[str | None] = mapped_column(String(45), nullable=True)
    details: Mapped[dict[str, Any] | None] = mapped_column(JSONVariant, nullable=True)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)

    __table_args__ = (
        Index("ix_security_events_created_at", "created_at"),
        Index("ix_security_events_type_created_at", "type", "created_at"),
        Index("ix_security_events_user_id", "user_id"),
    )
