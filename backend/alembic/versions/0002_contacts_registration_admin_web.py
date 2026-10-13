"""contacts, self-registration and the web dashboard

Revision ID: 0002
Revises: 0001
Create Date: 2026-10-13 12:55:40.246183

Every column added to an existing table carries a server default, so the rows already there are
valid without a data migration: existing accounts are ``approved`` and created by an
administrator, and existing requests were opened by the guest.
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0002"
down_revision: str | Sequence[str] | None = "0001"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_PENDING = sa.text("status = 'pending'")


def upgrade() -> None:
    op.create_table(
        "admin_web_sessions",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("token_hash", sa.String(length=64), nullable=False),
        sa.Column("user_id", sa.Uuid(), nullable=False),
        sa.Column("csrf_token", sa.String(length=64), nullable=False),
        sa.Column("mfa_passed", sa.Boolean(), server_default=sa.text("false"), nullable=False),
        sa.Column("ip", sa.String(length=45), nullable=True),
        sa.Column("user_agent", sa.String(length=255), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("last_seen_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("revoked_at", sa.DateTime(timezone=True), nullable=True),
        sa.ForeignKeyConstraint(
            ["user_id"],
            ["users.id"],
            name=op.f("fk_admin_web_sessions_user_id_users"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_admin_web_sessions")),
        sa.UniqueConstraint("token_hash", name=op.f("uq_admin_web_sessions_token_hash")),
    )
    op.create_index(
        op.f("ix_admin_web_sessions_user_id"), "admin_web_sessions", ["user_id"], unique=False
    )

    op.create_table(
        "contact_invites",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("inviter_user_id", sa.Uuid(), nullable=False),
        sa.Column("invitee_email", sa.String(length=320), nullable=False),
        sa.Column("invitee_user_id", sa.Uuid(), nullable=True),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("responded_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["invitee_user_id"],
            ["users.id"],
            name=op.f("fk_contact_invites_invitee_user_id_users"),
            ondelete="SET NULL",
        ),
        sa.ForeignKeyConstraint(
            ["inviter_user_id"],
            ["users.id"],
            name=op.f("fk_contact_invites_inviter_user_id_users"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_contact_invites")),
    )
    op.create_index(
        "ix_contact_invites_invitee_email_status",
        "contact_invites",
        ["invitee_email", "status"],
        unique=False,
    )
    op.create_index(
        "ix_contact_invites_invitee_user_id_status",
        "contact_invites",
        ["invitee_user_id", "status"],
        unique=False,
    )
    op.create_index(
        "uq_contact_invites_pending_pair",
        "contact_invites",
        ["inviter_user_id", "invitee_email"],
        unique=True,
        postgresql_where=_PENDING,
        sqlite_where=_PENDING,
    )

    op.create_table(
        "contacts",
        sa.Column("user_id", sa.Uuid(), nullable=False),
        sa.Column("contact_user_id", sa.Uuid(), nullable=False),
        sa.Column("auto_accept", sa.Boolean(), server_default=sa.text("false"), nullable=False),
        sa.Column("auto_accept_changed_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.CheckConstraint("user_id <> contact_user_id", name=op.f("ck_contacts_not_self")),
        sa.ForeignKeyConstraint(
            ["contact_user_id"],
            ["users.id"],
            name=op.f("fk_contacts_contact_user_id_users"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["user_id"], ["users.id"], name=op.f("fk_contacts_user_id_users"), ondelete="CASCADE"
        ),
        sa.PrimaryKeyConstraint("user_id", "contact_user_id", name=op.f("pk_contacts")),
    )
    op.create_index(
        op.f("ix_contacts_contact_user_id"), "contacts", ["contact_user_id"], unique=False
    )

    op.add_column(
        "connection_requests",
        sa.Column("initiator", sa.String(length=8), server_default="guest", nullable=False),
    )
    op.add_column(
        "connection_requests",
        sa.Column("auto_accepted", sa.Boolean(), server_default=sa.text("false"), nullable=False),
    )
    op.alter_column(
        "connection_requests", "guest_device_id", existing_type=sa.UUID(), nullable=True
    )
    op.create_index(
        "ix_connection_requests_guest_user_status",
        "connection_requests",
        ["guest_user_id", "status"],
        unique=False,
    )

    op.add_column(
        "users",
        sa.Column(
            "approval_status", sa.String(length=16), server_default="approved", nullable=False
        ),
    )
    op.add_column(
        "users",
        sa.Column("registered_via", sa.String(length=8), server_default="admin", nullable=False),
    )
    op.add_column("users", sa.Column("approved_at", sa.DateTime(timezone=True), nullable=True))
    op.add_column("users", sa.Column("approved_by", sa.Uuid(), nullable=True))
    op.add_column("users", sa.Column("rejected_at", sa.DateTime(timezone=True), nullable=True))
    op.add_column("users", sa.Column("deleted_at", sa.DateTime(timezone=True), nullable=True))
    op.add_column("users", sa.Column("last_login_at", sa.DateTime(timezone=True), nullable=True))
    op.add_column("users", sa.Column("totp_secret", sa.String(length=64), nullable=True))
    op.add_column(
        "users", sa.Column("totp_enabled_at", sa.DateTime(timezone=True), nullable=True)
    )
    op.add_column("users", sa.Column("totp_last_step", sa.Integer(), nullable=True))
    op.create_index(
        "ix_users_approval_status_created_at",
        "users",
        ["approval_status", "created_at"],
        unique=False,
    )
    op.create_foreign_key(
        op.f("fk_users_approved_by_users"),
        "users",
        "users",
        ["approved_by"],
        ["id"],
        ondelete="SET NULL",
    )
    # Autogenerate does not see CHECK constraints on an existing table, so this one is by hand.
    op.create_check_constraint(
        op.f("ck_users_pending_inactive"),
        "users",
        "approval_status = 'approved' OR is_active = false",
    )


def downgrade() -> None:
    op.drop_constraint(op.f("ck_users_pending_inactive"), "users", type_="check")
    op.drop_constraint(op.f("fk_users_approved_by_users"), "users", type_="foreignkey")
    op.drop_index("ix_users_approval_status_created_at", table_name="users")
    for column in (
        "totp_last_step",
        "totp_enabled_at",
        "totp_secret",
        "last_login_at",
        "deleted_at",
        "rejected_at",
        "approved_by",
        "approved_at",
        "registered_via",
        "approval_status",
    ):
        op.drop_column("users", column)

    op.drop_index("ix_connection_requests_guest_user_status", table_name="connection_requests")
    # An offer nobody accepted never had a guest device, and version 0001 cannot hold it.
    op.execute("DELETE FROM connection_requests WHERE guest_device_id IS NULL")
    op.alter_column(
        "connection_requests", "guest_device_id", existing_type=sa.UUID(), nullable=False
    )
    op.drop_column("connection_requests", "auto_accepted")
    op.drop_column("connection_requests", "initiator")

    op.drop_index(op.f("ix_contacts_contact_user_id"), table_name="contacts")
    op.drop_table("contacts")
    op.drop_index(
        "uq_contact_invites_pending_pair",
        table_name="contact_invites",
        postgresql_where=_PENDING,
        sqlite_where=_PENDING,
    )
    op.drop_index("ix_contact_invites_invitee_user_id_status", table_name="contact_invites")
    op.drop_index("ix_contact_invites_invitee_email_status", table_name="contact_invites")
    op.drop_table("contact_invites")
    op.drop_index(op.f("ix_admin_web_sessions_user_id"), table_name="admin_web_sessions")
    op.drop_table("admin_web_sessions")
