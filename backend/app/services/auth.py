"""Login flow: password check, lockout, device registration/verification, token issuance."""

from datetime import datetime, timedelta

from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.config import Settings
from app.core.errors import ApiError, Unauthorized
from app.core.security import (
    AccessClaims,
    create_access_token,
    generate_opaque_secret,
    hash_opaque_secret,
    hash_password_async,
    password_needs_rehash,
    verify_opaque_secret,
    verify_password_async,
)
from app.models import Device, User
from app.models.enums import ApprovalStatus, DeviceStatus, SecurityEventType
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


async def authenticate_password(
    db: AsyncSession, email: str, password: str, ip: str | None, *, channel: str = "app"
) -> User:
    """Check an email and password: lockout, failure counting and the audit trail.

    Shared by the desktop sign-in and the web dashboard, so the two cannot drift apart and the
    lockout counts failures from both. Raises the documented error, after committing its audit
    row; returns the user only for a correct password on an account that may sign in.

    The approval states are checked **after** the password: ``account_pending`` and
    ``account_rejected`` are only ever said to somebody who already knows the password, so they
    cannot be used to find out which addresses have registered."""
    now = utcnow()
    user = await get_user_by_email(db, email)
    if user is None:
        # Equalise timing with a real verify, and off the loop like the real one.
        await verify_password_async(None, password)
        details = {"reason": "unknown_user", "email": email}
        if channel != "app":
            details["channel"] = channel
        record_event(db, SecurityEventType.LOGIN_FAILED, ip=ip, details=details)
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

    if not await verify_password_async(user.password_hash, password):
        if count_failure(user, now=now):
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

    if user.approval_status == ApprovalStatus.PENDING:
        raise await _fail(
            db,
            SecurityEventType.LOGIN_FAILED,
            user=user,
            device=None,
            ip=ip,
            reason="account_pending",
            error=ApiError(403, "account_pending", "Account is waiting for approval"),
        )
    if user.approval_status == ApprovalStatus.REJECTED:
        raise await _fail(
            db,
            SecurityEventType.LOGIN_FAILED,
            user=user,
            device=None,
            ip=ip,
            reason="account_rejected",
            error=ApiError(403, "account_rejected", "Account registration was not approved"),
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
    return user


def count_failure(user: User, *, now: datetime | None = None) -> bool:
    """One failed attempt against the account - a wrong password, or a wrong second-factor code
    in the web dashboard. Returns ``True`` when this attempt locked the account."""
    user.failed_logins += 1
    if user.failed_logins >= MAX_FAILED_LOGINS:
        user.locked_until = (now or utcnow()) + LOCKOUT_DURATION
        return True
    return False


def is_locked(user: User, *, now: datetime | None = None) -> bool:
    locked_until = ensure_utc(user.locked_until)
    return locked_until is not None and locked_until > (now or utcnow())


async def upgrade_password_hash(user: User, password: str) -> None:
    """Re-hash a just-verified password whose argon2 parameters are outdated (ADR-0007)."""
    if password_needs_rehash(user.password_hash):
        user.password_hash = await hash_password_async(password)


def complete_sign_in(user: User) -> None:
    """Every factor has passed: clear the failure counter and record the time.

    The web dashboard calls this only after the second factor, so a correct password followed by
    wrong codes keeps counting toward the lockout instead of resetting it."""
    user.failed_logins = 0
    user.locked_until = None
    user.last_login_at = utcnow()


async def mark_signed_in(user: User, password: str) -> None:
    """After every other check of a sign-in has passed: clear the failure counter, record the
    time and upgrade the password hash if its parameters are outdated. Separate from
    :func:`authenticate_password` so a sign-in that fails later (a wrong device secret) does not
    reset the lockout counter."""
    complete_sign_in(user)
    await upgrade_password_hash(user, password)


async def login(
    db: AsyncSession, settings: Settings, payload: LoginRequest, ip: str | None
) -> TokenResponse:
    user = await authenticate_password(db, payload.email, payload.password, ip)
    device, device_secret = await _resolve_device(db, user, payload.device, ip)
    await mark_signed_in(user, payload.password)

    device.name = payload.device.name
    device.os_version = payload.device.os_version
    device.os_build = payload.device.os_build
    device.last_seen_at = utcnow()

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
