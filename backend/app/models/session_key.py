import uuid
from datetime import datetime
from typing import Any

from sqlalchemy import ForeignKey, LargeBinary, String, Uuid
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import JSONVariant, UtcDateTime


class SessionKey(Base):
    """Tunnel secret and endpoint exchange material. The row is DELETED as soon as the session
    ends (TODO week 4: session service)."""

    __tablename__ = "session_keys"

    session_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("sessions.id", ondelete="CASCADE"), primary_key=True
    )
    secret: Mapped[bytes] = mapped_column(LargeBinary(32), nullable=False)
    guest_cert_fp: Mapped[str | None] = mapped_column(String(64), nullable=True)
    host_cert_fp: Mapped[str | None] = mapped_column(String(64), nullable=True)
    guest_candidates: Mapped[list[dict[str, Any]] | None] = mapped_column(
        JSONVariant, nullable=True
    )
    host_candidates: Mapped[list[dict[str, Any]] | None] = mapped_column(JSONVariant, nullable=True)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
