"""Login flow: password check, lockout, device registration/verification, token issuance."""

from datetime import timedelta

from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.config import Settings
from app.core.errors import ApiError, Unauthorized
from app.core.security import (
    AccessClaims,
    create_access_token,
    generate_opaque_secret,
    hash_opaque_secret,
    hash_password,
    password_needs_rehash,
    verify_opaque_secret,
    verify_password,
)
from app.models import Device, User
from app.models.enums import DeviceStatus, SecurityEventType
from app.schemas.auth import DeviceAuthOut, DeviceLogin, LoginRequest, TokenResponse, UserOut
from app.services.security_events import record_event
from app.services.tokens import issue_refresh_token
from app.services.users import get_user_by_email

MAX_FAILED_LOGINS = 10
LOCKOUT_DURATION = timedelta(minutes=15)


def build_token_response(
    settings: Settings,
    user: User,
    device: Device,
    refresh_token: str,
    device_secret: str | None = None,
) -> TokenResponse:
    access, expires_in = create_access_token(
        settings, AccessClaims(user_id=user.id, device_id=device.id, role=user.role)
    )
    return TokenResponse(
        access_token=access,
        refresh_token=refresh_token,
        expires_in=expires_in,
        user=UserOut.model_validate(user),
        device=DeviceAuthOut(id=device.id, name=device.name, secret=device_secret),
    )


async def _fail(
    db: AsyncSession,
    event_type: SecurityEventType,
    *,
    user: User | None,
    device: Device | None,
    ip: str | None,
    reason: str,
    error: ApiError,
) -> ApiError:
    """Persist the audit row (and any counter changes) before the caller raises."""
    record_event(
        db,
        event_type,
        user_id=user.id if user else None,
        device_id=device.id if device else None,
        ip=ip,
        details={"reason": reason},
    )
    await db.commit()
    return error


async def _resolve_device(
    db: AsyncSession, user: User, info: DeviceLogin, ip: str | None
) -> tuple[Device, str | None]:
    """Register a new device (returns its secret once) or verify an existing one."""
    if info.id is None:
        secret = generate_opaque_secret()
        device = Device(
            user_id=user.id,
            name=info.name,
            os_version=info.os_version,
            os_build=info.os_build,
            device_secret_hash=hash_opaque_secret(secret),
        )
        db.add(device)
        await db.flush()
        return device, secret

    device = await db.get(Device, info.id)
    owned = device is not None and device.user_id == user.id
    if (
        not owned
        or not info.secret
        or not verify_opaque_secret(device.device_secret_hash, info.secret)
    ):
        raise await _fail(
            db,
            SecurityEventType.LOGIN_FAILED,
            user=user,
            device=device if owned else None,
            ip=ip,
            reason="device_secret_invalid",
            error=Unauthorized("Unknown device or invalid device secret"),
        )
    if device.status == DeviceStatus.REVOKED:
        raise await _fail(
            db,
            SecurityEventType.LOGIN_FAILED,
            user=user,
            device=device,
            ip=ip,
            reason="device_revoked",
            error=ApiError(403, "device_revoked", "Device has been revoked"),
        )
    return device, None


async def login(
    db: AsyncSession, settings: Settings, payload: LoginRequest, ip: str | None
) -> TokenResponse:
    now = utcnow()
    user = await get_user_by_email(db, payload.email)
    if user is None:
        verify_password(None, payload.password)  # equalise timing with a real verify
        record_event(
            db,
            SecurityEventType.LOGIN_FAILED,
            ip=ip,
            details={"reason": "unknown_user", "email": payload.email},
        )
        await db.commit()
        raise ApiError(401, "invalid_credentials", "Invalid email or password")

    locked_until = ensure_utc(user.locked_until)
    if locked_until is not None:
        if locked_until > now:
            raise await _fail(
                db,
                SecurityEventType.LOGIN_LOCKED,
                user=user,
                device=None,
                ip=ip,
                reason="locked",
                error=ApiError(423, "account_locked", "Account is temporarily locked"),
            )
        user.locked_until = None
        user.failed_logins = 0

    if not verify_password(user.password_hash, payload.password):
        user.failed_logins += 1
        if user.failed_logins >= MAX_FAILED_LOGINS:
            user.locked_until = now + LOCKOUT_DURATION
            raise await _fail(
                db,
                SecurityEventType.LOGIN_LOCKED,
                user=user,
                device=None,
                ip=ip,
                reason="too_many_failures",
                error=ApiError(423, "account_locked", "Account is temporarily locked"),
            )
        raise await _fail(
            db,
            SecurityEventType.LOGIN_FAILED,
            user=user,
            device=None,
            ip=ip,
            reason="bad_password",
            error=ApiError(401, "invalid_credentials", "Invalid email or password"),
        )

    if not user.is_active:
        raise await _fail(
            db,
            SecurityEventType.LOGIN_FAILED,
            user=user,
            device=None,
            ip=ip,
            reason="account_disabled",
            error=ApiError(403, "account_disabled", "Account is disabled"),
        )

    device, device_secret = await _resolve_device(db, user, payload.device, ip)

    user.failed_logins = 0
    user.locked_until = None
    if password_needs_rehash(user.password_hash):
        user.password_hash = hash_password(payload.password)
    device.name = payload.device.name
    device.os_version = payload.device.os_version
    device.os_build = payload.device.os_build
    device.last_seen_at = now

    refresh_token = await issue_refresh_token(db, settings, user.id, device.id)
    record_event(
        db,
        SecurityEventType.LOGIN_SUCCESS,
        user_id=user.id,
        device_id=device.id,
        ip=ip,
        details={"registered_device": device_secret is not None},
    )
    await db.commit()
    return build_token_response(settings, user, device, refresh_token, device_secret)
