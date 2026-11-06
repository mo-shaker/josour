import uuid
from datetime import datetime

from sqlalchemy import Boolean, ForeignKey, Index, String, Uuid, false, text
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import UtcDateTime
from app.models.enums import InviteStatus, WorkspaceRole

_DEFAULT = text("is_default")
_PENDING = text("status = 'pending'")


class Workspace(Base):
    """A company, a family or one person: the boundary of trust and, later, of billing (ADR-0018).

    Every account belongs to exactly one workspace (``users.workspace_id``). Contacts can only
    be made inside a workspace, and every request, offer and session needs a contact, so nothing
    is shared across the boundary. In ``single`` mode every account is in the default workspace,
    which is what a self-hosted server always was."""

    __tablename__ = "workspaces"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    name: Mapped[str] = mapped_column(String(100), nullable=False)
    is_default: Mapped[bool] = mapped_column(
        Boolean, nullable=False, default=False, server_default=false()
    )
    """The workspace ``single`` mode puts everybody in. At most one (a partial unique index)."""
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)

    __table_args__ = (
        Index(
            "uq_workspaces_default",
            "is_default",
            unique=True,
            postgresql_where=_DEFAULT,
            sqlite_where=_DEFAULT,
        ),
    )


class WorkspaceInvite(Base):
    """An invitation to join a workspace, addressed to an email like a contact invitation
    (ADR-0018).

    The invitee sees it under ``GET /me/workspace-invites`` once an account with that address
    exists, and accepting moves the account into the workspace. Sending one answers the same
    whether or not the address has an account."""

    __tablename__ = "workspace_invites"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    workspace_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("workspaces.id", ondelete="CASCADE"), nullable=False
    )
    email: Mapped[str] = mapped_column(String(320), nullable=False)
    role: Mapped[str] = mapped_column(String(8), nullable=False, default=WorkspaceRole.MEMBER)
    invited_by_user_id: Mapped[uuid.UUID | None] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="SET NULL"), nullable=True
    )
    status: Mapped[str] = mapped_column(String(16), nullable=False, default=InviteStatus.PENDING)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
    responded_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
    expires_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False)

    __table_args__ = (
        Index(
            "uq_workspace_invites_pending_email",
            "workspace_id",
            "email",
            unique=True,
            postgresql_where=_PENDING,
            sqlite_where=_PENDING,
        ),
        Index("ix_workspace_invites_email_status", "email", "status"),
    )
