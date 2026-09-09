from typing import Any

from sqlalchemy import String
from sqlalchemy.orm import Mapped, mapped_column

from app.db.base import Base
from app.db.types import JSONVariant

# Defaults per docs/api.md; rows override them. Read through app.services.app_settings.
DEFAULT_SETTINGS: dict[str, Any] = {
    "max_session_minutes": 120,
    "request_timeout_seconds": 60,
    "connect_timeout_seconds": 30,
    "log_domains": False,
    # ADR-0010: the allow-list is an optional restriction, off by default. The product exists to
    # reach sites that only serve the host's country, and a hand-curated list defeats that - one
    # page pulls from dozens of subdomains. Switching this on restricts a deployment to the list.
    "enforce_allowlist": False,
    "allowed_ports": [80, 443],
}


class AppSetting(Base):
    __tablename__ = "app_settings"

    key: Mapped[str] = mapped_column(String(64), primary_key=True)
    value: Mapped[Any] = mapped_column(JSONVariant, nullable=False)
