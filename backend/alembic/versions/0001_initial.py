"""initial

Revision ID: 0001
Revises:
Create Date: 2026-09-03 15:00:14.498526

"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import postgresql

from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0001"
down_revision: str | Sequence[str] | None = None
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:

    op.create_table(
        "allowed_domains",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("entry", sa.String(length=255), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        sa.Column("note", sa.String(length=255), nullable=True),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_allowed_domains")),
        sa.UniqueConstraint("entry", name=op.f("uq_allowed_domains_entry")),
    )
    op.create_table(
        "app_settings",
        sa.Column("key", sa.String(length=64), nullable=False),
        sa.Column(
            "value",
            sa.JSON().with_variant(postgresql.JSONB(astext_type=sa.Text()), "postgresql"),
            nullable=False,
        ),
        sa.PrimaryKeyConstraint("key", name=op.f("pk_app_settings")),
    )
    op.create_table(
        "users",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("email", sa.String(length=320), nullable=False),
        sa.Column("password_hash", sa.String(length=255), nullable=False),
        sa.Column("display_name", sa.String(length=100), nullable=False),
        sa.Column("role", sa.String(length=16), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        sa.Column("failed_logins", sa.Integer(), nullable=False),
        sa.Column("locked_until", sa.DateTime(timezone=True), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_users")),
        sa.UniqueConstraint("email", name=op.f("uq_users_email")),
    )
    op.create_table(
        "allowlist_versions",
        sa.Column("version", sa.Integer(), autoincrement=False, nullable=False),
        sa.Column(
            "entries",
            sa.JSON().with_variant(postgresql.JSONB(astext_type=sa.Text()), "postgresql"),
            nullable=False,
        ),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("created_by", sa.Uuid(), nullable=True),
        sa.ForeignKeyConstraint(
            ["created_by"],
            ["users.id"],
            name=op.f("fk_allowlist_versions_created_by_users"),
            ondelete="SET NULL",
        ),
        sa.PrimaryKeyConstraint("version", name=op.f("pk_allowlist_versions")),
    )
    op.create_table(
        "devices",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("user_id", sa.Uuid(), nullable=False),
        sa.Column("name", sa.String(length=100), nullable=False),
        sa.Column("os_version", sa.String(length=100), nullable=False),
        sa.Column("os_build", sa.String(length=50), nullable=True),
        sa.Column("device_secret_hash", sa.String(length=64), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("last_seen_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["user_id"], ["users.id"], name=op.f("fk_devices_user_id_users"), ondelete="CASCADE"
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_devices")),
    )
    op.create_index(op.f("ix_devices_user_id"), "devices", ["user_id"], unique=False)
    op.create_table(
        "connection_requests",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("guest_user_id", sa.Uuid(), nullable=False),
        sa.Column("guest_device_id", sa.Uuid(), nullable=False),
        sa.Column("host_user_id", sa.Uuid(), nullable=False),
        sa.Column("host_device_id", sa.Uuid(), nullable=False),
        sa.Column("requested_minutes", sa.Integer(), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("responded_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["guest_device_id"],
            ["devices.id"],
            name=op.f("fk_connection_requests_guest_device_id_devices"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["guest_user_id"],
            ["users.id"],
            name=op.f("fk_connection_requests_guest_user_id_users"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["host_device_id"],
            ["devices.id"],
            name=op.f("fk_connection_requests_host_device_id_devices"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["host_user_id"],
            ["users.id"],
            name=op.f("fk_connection_requests_host_user_id_users"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_connection_requests")),
    )
    op.create_index(
        "ix_connection_requests_guest_device_status",
        "connection_requests",
        ["guest_device_id", "status"],
        unique=False,
    )
    op.create_index(
        "ix_connection_requests_host_device_status",
        "connection_requests",
        ["host_device_id", "status"],
        unique=False,
    )
    op.create_table(
        "presence",
        sa.Column("device_id", sa.Uuid(), nullable=False),
        sa.Column("connected", sa.Boolean(), nullable=False),
        sa.Column("is_available_host", sa.Boolean(), nullable=False),
        sa.Column("public_ip", sa.String(length=45), nullable=True),
        sa.Column("listen_port", sa.Integer(), nullable=True),
        sa.Column("reachable", sa.Boolean(), nullable=True),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["device_id"],
            ["devices.id"],
            name=op.f("fk_presence_device_id_devices"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("device_id", name=op.f("pk_presence")),
    )
    op.create_table(
        "refresh_tokens",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("user_id", sa.Uuid(), nullable=False),
        sa.Column("device_id", sa.Uuid(), nullable=False),
        sa.Column("token_hash", sa.String(length=64), nullable=False),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("revoked_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["device_id"],
            ["devices.id"],
            name=op.f("fk_refresh_tokens_device_id_devices"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["user_id"],
            ["users.id"],
            name=op.f("fk_refresh_tokens_user_id_users"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_refresh_tokens")),
        sa.UniqueConstraint("token_hash", name=op.f("uq_refresh_tokens_token_hash")),
    )
    op.create_index(
        op.f("ix_refresh_tokens_device_id"), "refresh_tokens", ["device_id"], unique=False
    )
    op.create_index(op.f("ix_refresh_tokens_user_id"), "refresh_tokens", ["user_id"], unique=False)
    op.create_table(
        "security_events",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("type", sa.String(length=64), nullable=False),
        sa.Column("user_id", sa.Uuid(), nullable=True),
        sa.Column("device_id", sa.Uuid(), nullable=True),
        sa.Column("ip", sa.String(length=45), nullable=True),
        sa.Column(
            "details",
            sa.JSON().with_variant(postgresql.JSONB(astext_type=sa.Text()), "postgresql"),
            nullable=True,
        ),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["device_id"],
            ["devices.id"],
            name=op.f("fk_security_events_device_id_devices"),
            ondelete="SET NULL",
        ),
        sa.ForeignKeyConstraint(
            ["user_id"],
            ["users.id"],
            name=op.f("fk_security_events_user_id_users"),
            ondelete="SET NULL",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_security_events")),
    )
    op.create_index(
        "ix_security_events_created_at", "security_events", ["created_at"], unique=False
    )
    op.create_index(
        "ix_security_events_type_created_at",
        "security_events",
        ["type", "created_at"],
        unique=False,
    )
    op.create_index("ix_security_events_user_id", "security_events", ["user_id"], unique=False)
    op.create_table(
        "sessions",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("request_id", sa.Uuid(), nullable=False),
        sa.Column("guest_user_id", sa.Uuid(), nullable=False),
        sa.Column("guest_device_id", sa.Uuid(), nullable=False),
        sa.Column("host_user_id", sa.Uuid(), nullable=False),
        sa.Column("host_device_id", sa.Uuid(), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("started_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("ended_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("end_reason", sa.String(length=32), nullable=True),
        sa.Column("bytes_up", sa.BigInteger(), nullable=False),
        sa.Column("bytes_down", sa.BigInteger(), nullable=False),
        sa.Column("connect_result", sa.String(length=16), nullable=True),
        sa.Column("winner_type", sa.String(length=16), nullable=True),
        sa.Column("tls_version", sa.String(length=8), nullable=True),
        sa.Column("connect_ms", sa.Integer(), nullable=True),
        sa.ForeignKeyConstraint(
            ["guest_device_id"],
            ["devices.id"],
            name=op.f("fk_sessions_guest_device_id_devices"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["guest_user_id"],
            ["users.id"],
            name=op.f("fk_sessions_guest_user_id_users"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["host_device_id"],
            ["devices.id"],
            name=op.f("fk_sessions_host_device_id_devices"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["host_user_id"],
            ["users.id"],
            name=op.f("fk_sessions_host_user_id_users"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["request_id"],
            ["connection_requests.id"],
            name=op.f("fk_sessions_request_id_connection_requests"),
            ondelete="RESTRICT",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_sessions")),
        sa.UniqueConstraint("request_id", name=op.f("uq_sessions_request_id")),
    )
    op.create_index(
        "ix_sessions_status_created_at", "sessions", ["status", "created_at"], unique=False
    )
    op.create_index(
        "uq_sessions_live_guest_device_id",
        "sessions",
        ["guest_device_id"],
        unique=True,
        postgresql_where=sa.text("status IN ('connecting', 'active')"),
        sqlite_where=sa.text("status IN ('connecting', 'active')"),
    )
    op.create_index(
        "uq_sessions_live_guest_user_id",
        "sessions",
        ["guest_user_id"],
        unique=True,
        postgresql_where=sa.text("status IN ('connecting', 'active')"),
        sqlite_where=sa.text("status IN ('connecting', 'active')"),
    )
    op.create_index(
        "uq_sessions_live_host_device_id",
        "sessions",
        ["host_device_id"],
        unique=True,
        postgresql_where=sa.text("status IN ('connecting', 'active')"),
        sqlite_where=sa.text("status IN ('connecting', 'active')"),
    )
    op.create_index(
        "uq_sessions_live_host_user_id",
        "sessions",
        ["host_user_id"],
        unique=True,
        postgresql_where=sa.text("status IN ('connecting', 'active')"),
        sqlite_where=sa.text("status IN ('connecting', 'active')"),
    )
    op.create_table(
        "connect_diagnostics",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("session_id", sa.Uuid(), nullable=True),
        sa.Column("device_id", sa.Uuid(), nullable=False),
        sa.Column("role", sa.String(length=8), nullable=True),
        sa.Column(
            "data",
            sa.JSON().with_variant(postgresql.JSONB(astext_type=sa.Text()), "postgresql"),
            nullable=False,
        ),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["device_id"],
            ["devices.id"],
            name=op.f("fk_connect_diagnostics_device_id_devices"),
            ondelete="CASCADE",
        ),
        sa.ForeignKeyConstraint(
            ["session_id"],
            ["sessions.id"],
            name=op.f("fk_connect_diagnostics_session_id_sessions"),
            ondelete="SET NULL",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_connect_diagnostics")),
    )
    op.create_index(
        op.f("ix_connect_diagnostics_device_id"), "connect_diagnostics", ["device_id"], unique=False
    )
    op.create_index(
        op.f("ix_connect_diagnostics_session_id"),
        "connect_diagnostics",
        ["session_id"],
        unique=False,
    )
    op.create_table(
        "session_domains",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("session_id", sa.Uuid(), nullable=False),
        sa.Column("domain", sa.String(length=255), nullable=False),
        sa.Column("hit_count", sa.Integer(), nullable=False),
        sa.ForeignKeyConstraint(
            ["session_id"],
            ["sessions.id"],
            name=op.f("fk_session_domains_session_id_sessions"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_session_domains")),
        sa.UniqueConstraint("session_id", "domain", name="uq_session_domains_domain"),
    )
    op.create_index(
        op.f("ix_session_domains_session_id"), "session_domains", ["session_id"], unique=False
    )
    op.create_table(
        "session_keys",
        sa.Column("session_id", sa.Uuid(), nullable=False),
        sa.Column("secret", sa.LargeBinary(length=32), nullable=False),
        sa.Column("guest_cert_fp", sa.String(length=64), nullable=True),
        sa.Column("host_cert_fp", sa.String(length=64), nullable=True),
        sa.Column(
            "guest_candidates",
            sa.JSON().with_variant(postgresql.JSONB(astext_type=sa.Text()), "postgresql"),
            nullable=True,
        ),
        sa.Column(
            "host_candidates",
            sa.JSON().with_variant(postgresql.JSONB(astext_type=sa.Text()), "postgresql"),
            nullable=True,
        ),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["session_id"],
            ["sessions.id"],
            name=op.f("fk_session_keys_session_id_sessions"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("session_id", name=op.f("pk_session_keys")),
    )


def downgrade() -> None:

    op.drop_table("session_keys")
    op.drop_index(op.f("ix_session_domains_session_id"), table_name="session_domains")
    op.drop_table("session_domains")
    op.drop_index(op.f("ix_connect_diagnostics_session_id"), table_name="connect_diagnostics")
    op.drop_index(op.f("ix_connect_diagnostics_device_id"), table_name="connect_diagnostics")
    op.drop_table("connect_diagnostics")
    op.drop_index(
        "uq_sessions_live_host_user_id",
        table_name="sessions",
        postgresql_where=sa.text("status IN ('connecting', 'active')"),
        sqlite_where=sa.text("status IN ('connecting', 'active')"),
    )
    op.drop_index(
        "uq_sessions_live_host_device_id",
        table_name="sessions",
        postgresql_where=sa.text("status IN ('connecting', 'active')"),
        sqlite_where=sa.text("status IN ('connecting', 'active')"),
    )
    op.drop_index(
        "uq_sessions_live_guest_user_id",
        table_name="sessions",
        postgresql_where=sa.text("status IN ('connecting', 'active')"),
        sqlite_where=sa.text("status IN ('connecting', 'active')"),
    )
    op.drop_index(
        "uq_sessions_live_guest_device_id",
        table_name="sessions",
        postgresql_where=sa.text("status IN ('connecting', 'active')"),
        sqlite_where=sa.text("status IN ('connecting', 'active')"),
    )
    op.drop_index("ix_sessions_status_created_at", table_name="sessions")
    op.drop_table("sessions")
    op.drop_index("ix_security_events_user_id", table_name="security_events")
    op.drop_index("ix_security_events_type_created_at", table_name="security_events")
    op.drop_index("ix_security_events_created_at", table_name="security_events")
    op.drop_table("security_events")
    op.drop_index(op.f("ix_refresh_tokens_user_id"), table_name="refresh_tokens")
    op.drop_index(op.f("ix_refresh_tokens_device_id"), table_name="refresh_tokens")
    op.drop_table("refresh_tokens")
    op.drop_table("presence")
    op.drop_index("ix_connection_requests_host_device_status", table_name="connection_requests")
    op.drop_index("ix_connection_requests_guest_device_status", table_name="connection_requests")
    op.drop_table("connection_requests")
    op.drop_index(op.f("ix_devices_user_id"), table_name="devices")
    op.drop_table("devices")
    op.drop_table("allowlist_versions")
    op.drop_table("users")
    op.drop_table("app_settings")
    op.drop_table("allowed_domains")
