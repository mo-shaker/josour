import uuid
from datetime import datetime

from sqlalchemy import ForeignKey, Integer, String, Uuid
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import UtcDateTime


class PendingSignup(Base):
    """A ``verify``-mode registration that has not proved itself yet (ADR-0019).

    There is deliberately no ``users`` row until the codes have been entered: an address that
    already has an account and one that does not then behave identically to whoever registers
    it - neither can sign in, and only the owner of the mailbox ever sees a code. One row per
    address; registering again replaces it (new password, new code)."""

    __tablename__ = "pending_signups"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    email: Mapped[str] = mapped_column(String(320), nullable=False, unique=True)
    password_hash: Mapped[str] = mapped_column(String(255), nullable=False)
    display_name: Mapped[str] = mapped_column(String(100), nullable=False)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
    expires_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False)

    email_code_hash: Mapped[str] = mapped_column(String(64), nullable=False)
    email_code_sent_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False)
    email_attempts: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    email_sends: Mapped[int] = mapped_column(Integer, nullable=False, default=1)
    email_verified_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)

    token_hash: Mapped[str | None] = mapped_column(String(64), nullable=True, unique=True)
    """Handed out once the email is proved, for the phone steps (SHA-256 of the opaque value)."""
    phone: Mapped[str | None] = mapped_column(String(20), nullable=True)
    phone_code_hash: Mapped[str | None] = mapped_column(String(64), nullable=True)
    phone_code_sent_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    phone_attempts: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    phone_sends: Mapped[int] = mapped_column(Integer, nullable=False, default=0)


class PasswordReset(Base):
    """A mailed code that lets the owner of an address set a new password. One live row per
    account: asking again replaces it."""

    __tablename__ = "password_resets"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), nullable=False, unique=True
    )
    code_hash: Mapped[str] = mapped_column(String(64), nullable=False)
    attempts: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
    expires_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False)
