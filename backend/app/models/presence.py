import uuid
from datetime import datetime

from sqlalchemy import Boolean, ForeignKey, Integer, String, Uuid
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import UtcDateTime


class Presence(Base):
    """Live WebSocket state per device, owned by ``app.services.presence``; every row is reset
    on server boot because the registry behind it is in memory."""

    __tablename__ = "presence"

    device_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("devices.id", ondelete="CASCADE"), primary_key=True
    )
    connected: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    is_available_host: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    public_ip: Mapped[str | None] = mapped_column(String(45), nullable=True)
    # Port announced with host.available; target of the server-side reachability probe.
    listen_port: Mapped[int | None] = mapped_column(Integer, nullable=True)
    reachable: Mapped[bool | None] = mapped_column(Boolean, nullable=True)
    updated_at: Mapped[datetime] = mapped_column(
        UtcDateTime, nullable=False, default=utcnow, onupdate=utcnow
    )
