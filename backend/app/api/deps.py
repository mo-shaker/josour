"""FastAPI dependencies: settings, DB session, bearer authentication, admin check, login
rate limit."""

from dataclasses import dataclass
from typing import Annotated

from fastapi import Depends, Request
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.config import Settings, get_settings
from app.core.errors import ApiError, Forbidden, RateLimited, Unauthorized
from app.core.rate_limit import TokenBucketLimiter
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
    """Admin-only routes (``/admin/*`` and, outside dev, ``/docs`` + ``/openapi.json``)."""
    if auth.user.role != UserRole.ADMIN:
        raise Forbidden("Admin role required")
    return auth


AdminDep = Annotated[AuthContext, Depends(require_admin)]


def client_ip(request: Request) -> str | None:
    """Peer address as seen by uvicorn (X-Forwarded-For is applied by uvicorn's proxy-headers
    middleware when the proxy is trusted; see the Dockerfile CMD)."""
    return request.client.host if request.client else None


def build_login_rate_limiter(settings: Settings) -> TokenBucketLimiter | None:
    """``None`` disables the limit (``RATE_LIMIT_ENABLED=false``, used by the test-suite)."""
    if not settings.rate_limit_enabled:
        return None
    return TokenBucketLimiter(settings.login_rate_limit_per_minute, 60.0)


async def login_rate_limit(request: Request) -> None:
    """5 requests/minute per client IP on ``POST /auth/login`` -> 429 ``rate_limited`` with
    ``Retry-After``. Keyed by ``client_ip`` so X-Forwarded-For counts exactly when uvicorn's
    proxy-headers middleware trusts the peer (the compose deployment behind Caddy)."""
    limiter: TokenBucketLimiter | None = getattr(request.app.state, "login_rate_limiter", None)
    if limiter is None:
        return
    decision = limiter.check(client_ip(request) or "unknown")
    if not decision.allowed:
        raise RateLimited(decision.retry_after_seconds)
