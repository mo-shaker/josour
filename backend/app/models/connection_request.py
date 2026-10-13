import uuid
from datetime import datetime

from sqlalchemy import Boolean, ForeignKey, Index, Integer, String, Uuid, false
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import UtcDateTime
from app.models.enums import RequestInitiator, RequestStatus


class ConnectionRequest(Base):
    """A pending question between two contacts: "may I use your connection?" (``initiator =
    guest``, ``request.create``) or "do you want to use mine?" (``initiator = host``,
    ``offer.create``).

    An offer is addressed to the guest *user*, not to one of their devices: it rings every device
    they have connected, and ``guest_device_id`` is filled in by whichever accepts first."""

    __tablename__ = "connection_requests"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    guest_user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), nullable=False
    )
    guest_device_id: Mapped[uuid.UUID | None] = mapped_column(
        Uuid, ForeignKey("devices.id", ondelete="CASCADE"), nullable=True
    )
    host_user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), nullable=False
    )
    host_device_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("devices.id", ondelete="CASCADE"), nullable=False
    )
    requested_minutes: Mapped[int] = mapped_column(Integer, nullable=False)
    status: Mapped[str] = mapped_column(String(16), nullable=False, default=RequestStatus.PENDING)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
    responded_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    expires_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False)
    initiator: Mapped[str] = mapped_column(
        String(8),
        nullable=False,
        default=RequestInitiator.GUEST,
        server_default=RequestInitiator.GUEST.value,
    )
    auto_accepted: Mapped[bool] = mapped_column(
        Boolean, nullable=False, default=False, server_default=false()
    )
    """The host's client answered from a standing trust grant without showing a prompt."""

    __table_args__ = (
        Index("ix_connection_requests_guest_device_status", "guest_device_id", "status"),
        Index("ix_connection_requests_host_device_status", "host_device_id", "status"),
        Index("ix_connection_requests_guest_user_status", "guest_user_id", "status"),
    )
