import uuid
from datetime import datetime

from sqlalchemy import Boolean, CheckConstraint, ForeignKey, Uuid, false, text
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import UtcDateTime


class Contact(Base):
    """One direction of a contact relationship (ADR-0014).

    Accepting an invitation writes **two** rows, ``(A, B)`` and ``(B, A)``, and removing the
    contact deletes both, so "who are my contacts" is a single-column lookup and each side owns
    its own ``auto_accept`` switch.

    ``(A, B).auto_accept`` means *A lets B use A's connection without asking each time*. It is
    A's decision about A's own address and nobody else can set it; A can withdraw it at any
    moment."""

    __tablename__ = "contacts"

    user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), primary_key=True
    )
    contact_user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), primary_key=True, index=True
    )
    auto_accept: Mapped[bool] = mapped_column(
        Boolean, nullable=False, default=False, server_default=false()
    )
    auto_accept_changed_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)

    __table_args__ = (CheckConstraint(text("user_id <> contact_user_id"), name="not_self"),)
