"""workspaces (ADR-0018) and verified registration and password reset (ADR-0019)

Revision ID: 0004
Revises: 0003
Create Date: 2026-11-06 23:39:59.010978

Every existing account goes into one default workspace, so a server that is upgraded keeps
behaving exactly as before: ``workspace_mode`` defaults to ``single``, every contact that exists
is between two members of that workspace, and nothing that worked stops working. The oldest
active administrator (or, failing that, the oldest account) is recorded as its owner, which only
matters if the operator later switches to ``multi``.

``users.workspace_id`` is added nullable, filled, and only then made NOT NULL.
"""

import uuid
from collections.abc import Sequence
from datetime import UTC, datetime

import sqlalchemy as sa

from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0004"
down_revision: str | Sequence[str] | None = "0003"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_PENDING = sa.text("status = 'pending'")
_DEFAULT = sa.text("is_default")
_HAS_PHONE = sa.text("phone IS NOT NULL")


def upgrade() -> None:
    op.create_table(
        "workspaces",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("name", sa.String(length=100), nullable=False),
        sa.Column("is_default", sa.Boolean(), server_default=sa.text("false"), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_workspaces")),
    )
    op.create_index(
        "uq_workspaces_default",
        "workspaces",
        ["is_default"],
        unique=True,
        postgresql_where=_DEFAULT,
        sqlite_where=_DEFAULT,
    )

    op.add_column("users", sa.Column("workspace_id", sa.Uuid(), nullable=True))
    op.add_column(
        "users",
        sa.Column("workspace_role", sa.String(length=8), server_default="member", nullable=False),
    )
    op.add_column(
        "users", sa.Column("email_verified_at", sa.DateTime(timezone=True), nullable=True)
    )
    op.add_column("users", sa.Column("phone", sa.String(length=20), nullable=True))
    op.add_column(
        "users", sa.Column("phone_verified_at", sa.DateTime(timezone=True), nullable=True)
    )

    # --- Data: everybody into the default workspace.
    bind = op.get_bind()
    if bind.execute(sa.text("SELECT 1 FROM users LIMIT 1")).first() is not None:
        workspace_id = uuid.uuid4()
        workspaces = sa.table(
            "workspaces",
            sa.column("id", sa.Uuid()),
            sa.column("name", sa.String()),
            sa.column("is_default", sa.Boolean()),
            sa.column("created_at", sa.DateTime(timezone=True)),
        )
        op.bulk_insert(
            workspaces,
            [
                {
                    "id": workspace_id,
                    "name": "Josour",
                    "is_default": True,
                    "created_at": datetime.now(UTC),
                }
            ],
        )
        bind.execute(
            sa.text("UPDATE users SET workspace_id = :ws").bindparams(
                sa.bindparam("ws", value=workspace_id, type_=sa.Uuid())
            )
        )
        owner = bind.execute(
            sa.text(
                "SELECT id FROM users WHERE deleted_at IS NULL "
                "ORDER BY (role = 'admin' AND is_active) DESC, created_at, id LIMIT 1"
            )
        ).first()
        if owner is not None:
            bind.execute(
                sa.text("UPDATE users SET workspace_role = 'owner' WHERE id = :id").bindparams(
                    sa.bindparam("id", value=owner[0], type_=sa.Uuid())
                )
            )

    op.alter_column("users", "workspace_id", existing_type=sa.Uuid(), nullable=False)
    op.create_index(op.f("ix_users_workspace_id"), "users", ["workspace_id"], unique=False)
    op.create_index(
        "uq_users_phone",
        "users",
        ["phone"],
        unique=True,
        postgresql_where=_HAS_PHONE,
        sqlite_where=_HAS_PHONE,
    )
    op.create_foreign_key(
        op.f("fk_users_workspace_id_workspaces"),
        "users",
        "workspaces",
        ["workspace_id"],
        ["id"],
        ondelete="RESTRICT",
    )

    op.create_table(
        "workspace_invites",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("workspace_id", sa.Uuid(), nullable=False),
        sa.Column("email", sa.String(length=320), nullable=False),
        sa.Column("role", sa.String(length=8), nullable=False),
        sa.Column("invited_by_user_id", sa.Uuid(), nullable=True),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("responded_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["invited_by_user_id"],
            ["users.id"],
            name=op.f("fk_workspace_invites_invited_by_user_id_users"),
            ondelete="SET NULL",
        ),
        sa.ForeignKeyConstraint(
            ["workspace_id"],
            ["workspaces.id"],
            name=op.f("fk_workspace_invites_workspace_id_workspaces"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_workspace_invites")),
    )
    op.create_index(
        "ix_workspace_invites_email_status", "workspace_invites", ["email", "status"], unique=False
    )
    op.create_index(
        "uq_workspace_invites_pending_email",
        "workspace_invites",
        ["workspace_id", "email"],
        unique=True,
        postgresql_where=_PENDING,
        sqlite_where=_PENDING,
    )

    op.create_table(
        "pending_signups",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("email", sa.String(length=320), nullable=False),
        sa.Column("password_hash", sa.String(length=255), nullable=False),
        sa.Column("display_name", sa.String(length=100), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("email_code_hash", sa.String(length=64), nullable=False),
        sa.Column("email_code_sent_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("email_attempts", sa.Integer(), nullable=False),
        sa.Column("email_sends", sa.Integer(), nullable=False),
        sa.Column("email_verified_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("token_hash", sa.String(length=64), nullable=True),
        sa.Column("phone", sa.String(length=20), nullable=True),
        sa.Column("phone_code_hash", sa.String(length=64), nullable=True),
        sa.Column("phone_code_sent_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("phone_attempts", sa.Integer(), nullable=False),
        sa.Column("phone_sends", sa.Integer(), nullable=False),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_pending_signups")),
        sa.UniqueConstraint("email", name=op.f("uq_pending_signups_email")),
        sa.UniqueConstraint("token_hash", name=op.f("uq_pending_signups_token_hash")),
    )
    op.create_table(
        "password_resets",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("user_id", sa.Uuid(), nullable=False),
        sa.Column("code_hash", sa.String(length=64), nullable=False),
        sa.Column("attempts", sa.Integer(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["user_id"],
            ["users.id"],
            name=op.f("fk_password_resets_user_id_users"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_password_resets")),
        sa.UniqueConstraint("user_id", name=op.f("uq_password_resets_user_id")),
    )


def downgrade() -> None:
    # Workspaces, pending registrations and reset codes are dropped. Contacts that crossed no
    # boundary keep working; any that a multi-mode server had kept apart become reachable again
    # only through new invitations, because contacts themselves are untouched.
    op.drop_table("password_resets")
    op.drop_table("pending_signups")
    op.drop_index(
        "uq_workspace_invites_pending_email",
        table_name="workspace_invites",
        postgresql_where=_PENDING,
        sqlite_where=_PENDING,
    )
    op.drop_index("ix_workspace_invites_email_status", table_name="workspace_invites")
    op.drop_table("workspace_invites")
    op.drop_constraint(op.f("fk_users_workspace_id_workspaces"), "users", type_="foreignkey")
    op.drop_index(
        "uq_users_phone", table_name="users", postgresql_where=_HAS_PHONE, sqlite_where=_HAS_PHONE
    )
    op.drop_index(op.f("ix_users_workspace_id"), table_name="users")
    op.drop_column("users", "phone_verified_at")
    op.drop_column("users", "phone")
    op.drop_column("users", "email_verified_at")
    op.drop_column("users", "workspace_role")
    op.drop_column("users", "workspace_id")
    op.drop_index(
        "uq_workspaces_default",
        table_name="workspaces",
        postgresql_where=_DEFAULT,
        sqlite_where=_DEFAULT,
    )
    op.drop_table("workspaces")
