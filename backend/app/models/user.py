import uuid
from datetime import datetime

from sqlalchemy import (
    Boolean,
    CheckConstraint,
    ForeignKey,
    Index,
    Integer,
    String,
    Uuid,
    text,
)
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import UtcDateTime
from app.models.enums import ApprovalStatus, RegisteredVia, UserRole


class User(Base):
    __tablename__ = "users"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    email: Mapped[str] = mapped_column(String(320), unique=True, nullable=False)
    password_hash: Mapped[str] = mapped_column(String(255), nullable=False)
    display_name: Mapped[str] = mapped_column(String(100), nullable=False)
    role: Mapped[str] = mapped_column(String(16), nullable=False, default=UserRole.USER)
    is_active: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True)
    failed_logins: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    locked_until: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)

    # --- Self-registration (ADR-0017). Every account that existed before it is ``approved``.
    approval_status: Mapped[str] = mapped_column(
        String(16),
        nullable=False,
        default=ApprovalStatus.APPROVED,
        server_default=ApprovalStatus.APPROVED.value,
    )
    registered_via: Mapped[str] = mapped_column(
        String(8),
        nullable=False,
        default=RegisteredVia.ADMIN,
        server_default=RegisteredVia.ADMIN.value,
    )
    approved_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    approved_by: Mapped[uuid.UUID | None] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="SET NULL"), nullable=True
    )
    rejected_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    deleted_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    """Set by the soft delete: the row stays so the other party's session history keeps a name,
    but its email, name and password are replaced and it can never sign in again."""
    last_login_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)

    # --- Second factor for the web dashboard (ADR-0016); unused by the desktop sign-in.
    totp_secret: Mapped[str | None] = mapped_column(String(64), nullable=True)
    totp_enabled_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    totp_last_step: Mapped[int | None] = mapped_column(Integer, nullable=True)
    """The last accepted TOTP time step, so a code cannot be replayed inside its window."""

    __table_args__ = (
        # A pending or rejected account can never be active, so every existing ``is_active``
        # check keeps it out without knowing approval exists.
        CheckConstraint(
            text("approval_status = 'approved' OR is_active = false"), name="pending_inactive"
        ),
        Index("ix_users_approval_status_created_at", "approval_status", "created_at"),
    )

    @property
    def is_deleted(self) -> bool:
        return self.deleted_at is not None

    @property
    def state(self) -> str:
        """One word for where the account stands: ``active``, ``disabled``, ``pending``,
        ``rejected`` or ``deleted`` (``app.services.users.UserState``)."""
        if self.deleted_at is not None:
            return "deleted"
        if self.approval_status == ApprovalStatus.PENDING:
            return "pending"
        if self.approval_status == ApprovalStatus.REJECTED:
            return "rejected"
        return "active" if self.is_active else "disabled"
