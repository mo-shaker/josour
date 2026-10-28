"""web dashboard: per-session TOTP failure count; mfa_passed means "a code was verified"

Revision ID: 0003
Revises: 0002
Create Date: 2026-10-28 12:00:00.000000

``admin_web_sessions.mfa_passed`` used to be set for a password-only sign-in when no second
factor was needed at that moment, so turning on ``ADMIN_WEB_REQUIRE_TOTP`` later left those
sessions through. It now means only that a TOTP code was verified in the session. The data step
clears it for every session whose administrator has no TOTP enrolled - such a session can never
have verified a code - and they keep working while TOTP is not required.
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0003"
down_revision: str | Sequence[str] | None = "0002"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column(
        "admin_web_sessions",
        sa.Column("totp_failures", sa.Integer(), server_default=sa.text("0"), nullable=False),
    )
    op.execute(
        sa.text(
            "UPDATE admin_web_sessions SET mfa_passed = false "
            "WHERE user_id IN (SELECT id FROM users WHERE totp_enabled_at IS NULL)"
        )
    )


def downgrade() -> None:
    # The data step is not reversed: a cleared mfa_passed only ever asks for a code again.
    op.drop_column("admin_web_sessions", "totp_failures")
