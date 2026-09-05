"""FastAPI dependencies: settings, DB session, bearer authentication, admin check, and the
rate limits of ADR-0008."""

import json
from dataclasses import dataclass
from typing import Annotated

from fastapi import Depends, Request
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.config import Settings, get_settings
from app.core.errors import ApiError, Forbidden, RateLimited, Unauthorized
from app.core.rate_limit import RateLimiters, TokenBucketLimiter, opaque_key
from app.core.security import AccessClaims, InvalidAccessToken, decode_access_token
from app.db.session import get_db
from app.models import Device, User
from app.models.enums import DeviceStatus, UserRole
from app.schemas.common import normalise_email_address

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


def build_rate_limiters(settings: Settings) -> RateLimiters | None:
    """``None`` disables every limit (``RATE_LIMIT_ENABLED=false``: test-suite, load harness)."""
    if not settings.rate_limit_enabled:
        return None
    return RateLimiters(
        login_ip=TokenBucketLimiter(settings.login_rate_limit_per_minute, 60.0),
        login_email=TokenBucketLimiter(
            settings.login_email_rate_limit,
            settings.login_email_rate_limit_window_minutes * 60.0,
        ),
        refresh_ip=TokenBucketLimiter(settings.refresh_rate_limit_per_minute, 60.0),
        probe_user=TokenBucketLimiter(settings.probe_rate_limit_per_minute, 60.0),
    )


def get_rate_limiters(request: Request) -> RateLimiters | None:
    return getattr(request.app.state, "rate_limiters", None)


def _spend(limiter: TokenBucketLimiter, key: str) -> None:
    decision = limiter.check(key)
    if not decision.allowed:
        raise RateLimited(decision.retry_after_seconds)


async def submitted_login_email(request: Request) -> str | None:
    """The ``email`` of a login body, normalised exactly as ``LoginRequest`` normalises it.

    Read straight off the request (starlette caches the body, so the handler still parses it)
    rather than declared as a body parameter, because the limit has to answer before validation:
    a malformed body must get the same 429 a well-formed one gets, not a 422 that tells an
    attacker their attempt was free. The value is a bucket key and nothing else - it is hashed
    immediately by the caller and never logged.
    """
    try:
        body = await request.json()
    except (json.JSONDecodeError, UnicodeDecodeError, ValueError):
        return None
    if not isinstance(body, dict):
        return None
    email = body.get("email")
    if not isinstance(email, str):
        return None
    email = email[:320]  # LoginRequest's own ceiling; the input is attacker-controlled
    try:
        return normalise_email_address(email)
    except ValueError:
        # Not an address, so the request will fail validation - but it still spent an attempt.
        return email.strip().lower()


async def login_rate_limit(request: Request) -> None:
    """``POST /auth/login``: per client IP *and* per submitted email (ADR-0008).

    The IP bucket is a coarse anti-flood cap; the email bucket is what stops twenty addresses
    hammering one account, and what keeps a colleague's typo from spending the office's shared
    budget. Both answer with 429 ``rate_limited`` and ``Retry-After``. Keyed by ``client_ip``, so
    X-Forwarded-For counts exactly when uvicorn's proxy-headers middleware trusts the peer (the
    compose deployment behind Caddy).
    """
    limiters = get_rate_limiters(request)
    if limiters is None:
        return
    _spend(limiters.login_ip, client_ip(request) or "unknown")
    email = await submitted_login_email(request)
    if email is not None:
        _spend(limiters.login_email, opaque_key(email))


def refund_login_attempt(request: Request, email: str) -> None:
    """Hand the email bucket's token back after a successful login, so that only *failures*
    durably spend it (ADR-0008). This is what keeps the bucket comparable with the account
    lockout counter, and what keeps a legitimate user from ever meeting the limit."""
    limiters = get_rate_limiters(request)
    if limiters is not None:
        limiters.login_email.refund(opaque_key(email))


async def refresh_rate_limit(request: Request) -> None:
    """``POST /auth/refresh``: lenient per-IP cap. The path is unauthenticated but cheap (a
    SHA-256 and two queries, no KDF), so this bounds flooding, not guessing."""
    limiters = get_rate_limiters(request)
    if limiters is None:
        return
    _spend(limiters.refresh_ip, client_ip(request) or "unknown")


async def probe_rate_limit(request: Request, auth: AuthDep) -> None:
    """``POST /probe``: per authenticated user, because the endpoint makes the server open a TCP
    socket to a caller-named public address and the accountable unit is the identity, not the
    network it came from."""
    limiters = get_rate_limiters(request)
    if limiters is None:
        return
    _spend(limiters.probe_user, str(auth.user.id))
