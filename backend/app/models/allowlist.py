import uuid
from datetime import datetime

from sqlalchemy import Boolean, ForeignKey, Integer, String, Uuid
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import JSONVariant, UtcDateTime


class AllowedDomain(Base):
    """Current editable allowlist. ``entry`` is ``example.com`` (with subdomains), ``=exact.com``
    or ``host:port``; see docs/protocol.md section 6."""

    __tablename__ = "allowed_domains"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    entry: Mapped[str] = mapped_column(String(255), unique=True, nullable=False)
    is_active: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    note: Mapped[str | None] = mapped_column(String(255), nullable=True)
    updated_at: Mapped[datetime] = mapped_column(
        UtcDateTime, nullable=False, default=utcnow, onupdate=utcnow
    )


class AllowlistVersion(Base):
    """Immutable published snapshot of the active entries; clients cache by ``version``."""

    __tablename__ = "allowlist_versions"

    version: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=False)
    entries: Mapped[list[str]] = mapped_column(JSONVariant, nullable=False)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
    created_by: Mapped[uuid.UUID | None] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="SET NULL"), nullable=True
    )
