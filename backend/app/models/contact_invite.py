import uuid
from datetime import datetime

from sqlalchemy import ForeignKey, Index, String, Uuid, text
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import UtcDateTime
from app.models.enums import InviteStatus

_PENDING = text("status = 'pending'")


class ContactInvite(Base):
    """An "add me as a contact" request, addressed to an email rather than to an account.

    Addressing the email is what lets an invitation answer identically whether or not the
    address has an account (ADR-0014): the row is written either way, and it is attached to the
    account (``invitee_user_id``) only once that account exists and has been approved."""

    __tablename__ = "contact_invites"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    inviter_user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), nullable=False
    )
    invitee_email: Mapped[str] = mapped_column(String(320), nullable=False)
    invitee_user_id: Mapped[uuid.UUID | None] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="SET NULL"), nullable=True
    )
    status: Mapped[str] = mapped_column(String(16), nullable=False, default=InviteStatus.PENDING)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
    responded_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    expires_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False)

    __table_args__ = (
        Index(
            "uq_contact_invites_pending_pair",
            "inviter_user_id",
            "invitee_email",
            unique=True,
            postgresql_where=_PENDING,
            sqlite_where=_PENDING,
        ),
        Index("ix_contact_invites_invitee_email_status", "invitee_email", "status"),
        Index("ix_contact_invites_invitee_user_id_status", "invitee_user_id", "status"),
    )
