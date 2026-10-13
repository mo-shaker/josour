"""Import every model so ``Base.metadata`` is complete for Alembic and ``create_all``."""

from app.db.base import Base
from app.models.admin_web_session import AdminWebSession
from app.models.allowlist import AllowedDomain, AllowlistVersion
from app.models.app_setting import AppSetting
from app.models.connect_diagnostic import ConnectDiagnostic
from app.models.connection_request import ConnectionRequest
from app.models.contact import Contact
from app.models.contact_invite import ContactInvite
from app.models.device import Device
from app.models.presence import Presence
from app.models.refresh_token import RefreshToken
from app.models.security_event import SecurityEvent
from app.models.session import Session
from app.models.session_domain import SessionDomain
from app.models.session_key import SessionKey
from app.models.user import User

__all__ = [
    "AdminWebSession",
    "AllowedDomain",
    "AllowlistVersion",
    "AppSetting",
    "Base",
    "ConnectDiagnostic",
    "ConnectionRequest",
    "Contact",
    "ContactInvite",
    "Device",
    "Presence",
    "RefreshToken",
    "SecurityEvent",
    "Session",
    "SessionDomain",
    "SessionKey",
    "User",
]
