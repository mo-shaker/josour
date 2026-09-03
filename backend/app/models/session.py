import uuid
from datetime import datetime

from sqlalchemy import BigInteger, ForeignKey, Index, Integer, String, Uuid, text
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import UtcDateTime
from app.models.enums import SessionStatus

_NON_ENDED = text("status IN ('connecting', 'active')")


def _one_live_session_per(column: str) -> Index:
    """Partial unique index: at most one non-ended session per user / per device."""
    return Index(
        f"uq_sessions_live_{column}",
        column,
        unique=True,
        postgresql_where=_NON_ENDED,
        sqlite_where=_NON_ENDED,
    )


class Session(Base):
    __tablename__ = "sessions"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    request_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("connection_requests.id", ondelete="RESTRICT"), nullable=False, unique=True
    )
    guest_user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), nullable=False
    )
    guest_device_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("devices.id", ondelete="CASCADE"), nullable=False
    )
    host_user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), nullable=False
    )
    host_device_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("devices.id", ondelete="CASCADE"), nullable=False
    )
    status: Mapped[str] = mapped_column(
        String(16), nullable=False, default=SessionStatus.CONNECTING
    )
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
    started_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    expires_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False)
    ended_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    end_reason: Mapped[str | None] = mapped_column(String(32), nullable=True)
    bytes_up: Mapped[int] = mapped_column(BigInteger, nullable=False, default=0)
    bytes_down: Mapped[int] = mapped_column(BigInteger, nullable=False, default=0)
    connect_result: Mapped[str | None] = mapped_column(String(16), nullable=True)
    winner_type: Mapped[str | None] = mapped_column(String(16), nullable=True)
    tls_version: Mapped[str | None] = mapped_column(String(8), nullable=True)
    connect_ms: Mapped[int | None] = mapped_column(Integer, nullable=True)

    __table_args__ = (
        _one_live_session_per("guest_user_id"),
        _one_live_session_per("host_user_id"),
        _one_live_session_per("guest_device_id"),
        _one_live_session_per("host_device_id"),
        Index("ix_sessions_status_created_at", "status", "created_at"),
    )
