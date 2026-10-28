import uuid
from datetime import datetime

from sqlalchemy import Boolean, ForeignKey, Integer, String, Uuid, false, text
from sqlalchemy.orm import Mapped, mapped_column

from app.core.clock import utcnow
from app.db.base import Base
from app.db.types import UtcDateTime


class AdminWebSession(Base):
    """A signed-in browser of the web dashboard (ADR-0016).

    Kept server-side rather than in a signed cookie so that it can be revoked: changing an
    administrator's password, disabling or demoting them ends every dashboard session they hold.
    The cookie carries an opaque token; only its SHA-256 is stored, like refresh tokens."""

    __tablename__ = "admin_web_sessions"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    token_hash: Mapped[str] = mapped_column(String(64), unique=True, nullable=False)
    user_id: Mapped[uuid.UUID] = mapped_column(
        Uuid, ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    csrf_token: Mapped[str] = mapped_column(String(64), nullable=False)
    mfa_passed: Mapped[bool] = mapped_column(
        Boolean, nullable=False, default=False, server_default=false()
    )
    """A TOTP code was verified in *this* session. Never set by the password alone, so a
    session that only knows the password is still sent to the code (or enrolment) page if the
    administrator enables TOTP or the operator turns on ``ADMIN_WEB_REQUIRE_TOTP`` later."""
    totp_failures: Mapped[int] = mapped_column(
        Integer, nullable=False, default=0, server_default=text("0")
    )
    """Wrong TOTP codes submitted in this session; the session is ended at
    ``app.web.views.MAX_TOTP_FAILURES``."""
    ip: Mapped[str | None] = mapped_column(String(45), nullable=True)
    user_agent: Mapped[str | None] = mapped_column(String(255), nullable=True)
    created_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
    last_seen_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False, default=utcnow)
    expires_at: Mapped[datetime] = mapped_column(UtcDateTime, nullable=False)
    revoked_at: Mapped[datetime | None] = mapped_column(UtcDateTime, nullable=True)
