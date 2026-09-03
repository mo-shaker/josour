from typing import Any

from sqlalchemy import String
from sqlalchemy.orm import Mapped, mapped_column

from app.db.base import Base
from app.db.types import JSONVariant

# Defaults per docs/api.md; rows override them (TODO week 2: admin settings router).
DEFAULT_SETTINGS: dict[str, Any] = {
    "max_session_minutes": 120,
    "request_timeout_seconds": 60,
    "connect_timeout_seconds": 30,
    "log_domains": False,
    "allowed_ports": [80, 443],
}


class AppSetting(Base):
    __tablename__ = "app_settings"

    key: Mapped[str] = mapped_column(String(64), primary_key=True)
    value: Mapped[Any] = mapped_column(JSONVariant, nullable=False)
