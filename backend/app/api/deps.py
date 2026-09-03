"""FastAPI dependencies: settings, DB session, bearer authentication, admin check."""

from dataclasses import dataclass
from typing import Annotated

from fastapi import Depends, Request
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.config import Settings, get_settings
from app.core.errors import ApiError, Forbidden, Unauthorized
from app.core.security import AccessClaims, InvalidAccessToken, decode_access_token
from app.db.session import get_db
from app.models import Device, User
from app.models.enums import DeviceStatus, UserRole

SettingsDep = Annotated[Settings, Depends(get_settings)]
DbDep = Annotated[AsyncSession, Depends(get_db)]

_bearer = HTTPBearer(auto_error=False, description="Access JWT from POST /auth/login")


@dataclass(slots=True)
class AuthContext:
    user: User
    device: Device
    claims: AccessClaims


async def get_auth(
    credentials: Annotated[HTTPAuthorizationCredentials | None, Depends(_bearer)],
    db: DbDep,
    settings: SettingsDep,
) -> AuthContext:
    if credentials is None or credentials.scheme.lower() != "bearer":
        raise Unauthorized("Missing bearer token")
    try:
        claims = decode_access_token(settings, credentials.credentials)
    except InvalidAccessToken as exc:
        raise Unauthorized("Invalid or expired access token") from exc

    user = await db.get(User, claims.user_id)
    if user is None or not user.is_active:
        raise Unauthorized("Account is not active")
    device = await db.get(Device, claims.device_id)
    if device is None or device.user_id != user.id:
        raise Unauthorized("Unknown device")
    if device.status == DeviceStatus.REVOKED:
        raise ApiError(403, "device_revoked", "Device has been revoked")
    return AuthContext(user=user, device=device, claims=claims)


AuthDep = Annotated[AuthContext, Depends(get_auth)]


async def require_admin(auth: AuthDep) -> AuthContext:
    """Admin-only routes (TODO week 2: admin_* routers)."""
    if auth.user.role != UserRole.ADMIN:
        raise Forbidden("Admin role required")
    return auth


AdminDep = Annotated[AuthContext, Depends(require_admin)]


def client_ip(request: Request) -> str | None:
    """Peer address as seen by uvicorn (X-Forwarded-For is applied by uvicorn's proxy-headers
    middleware when the proxy is trusted; see the Dockerfile CMD)."""
    return request.client.host if request.client else None
