"""User lookup, creation and admin updates. Callers commit."""

import uuid
from typing import Any

from sqlalchemy import or_, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.errors import Conflict, ValidationFailed
from app.core.security import hash_password_async
from app.models import Device, Presence, User
from app.models.enums import DeviceStatus, SecurityEventType, UserRole
from app.schemas.admin import AdminUserPatch
from app.services.security_events import record_event
from app.services.tokens import revoke_user_tokens

MIN_PASSWORD_LENGTH = 8


def normalise_email(email: str) -> str:
    return email.strip().lower()


def validate_password(password: str) -> None:
    if len(password) < MIN_PASSWORD_LENGTH:
        raise ValidationFailed(f"password must be at least {MIN_PASSWORD_LENGTH} characters")


def _escape_like(value: str) -> str:
    return value.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")


async def list_users(db: AsyncSession, *, q: str | None = None, limit: int = 100) -> list[User]:
    """Newest first; ``q`` is a case-insensitive substring match on email or display_name."""
    stmt = select(User).order_by(User.created_at.desc(), User.email).limit(limit)
    if q and q.strip():
        pattern = f"%{_escape_like(q.strip())}%"
        stmt = stmt.where(
            or_(
                User.email.ilike(pattern, escape="\\"),
                User.display_name.ilike(pattern, escape="\\"),
            )
        )
    return list(await db.scalars(stmt))


async def update_user(
    db: AsyncSession,
    user: User,
    patch: AdminUserPatch,
    *,
    actor_user_id: uuid.UUID | None = None,
    ip: str | None = None,
) -> list[uuid.UUID]:
    """``PATCH /admin/users/{id}``. Returns the devices whose live control channel the caller must
    close (see below); commits are the caller's.

    Deactivating or changing the password revokes every live refresh token, so the user's devices
    must sign in again.

    **Deactivating does more than that, and has to.** A refresh token is only consulted when an
    access token expires, so revoking tokens alone left a disabled user browsing through somebody
    else's connection until their current access token ran out - the administrator pressed the
    button and nothing observable happened. So deactivation now clears the user's presence and
    hands back their device ids, exactly as revoking a single device does, and the caller drops
    those control channels with 4403. The account state and what the network is doing agree again.
    """
    changes = patch.model_dump(exclude_unset=True)
    if changes.get("display_name") is not None:
        user.display_name = changes["display_name"]
    if changes.get("password") is not None:
        validate_password(changes["password"])
        user.password_hash = await hash_password_async(changes["password"])
        await revoke_user_tokens(db, user.id)

    disconnect: list[uuid.UUID] = []
    if changes.get("is_active") is not None and changes["is_active"] != user.is_active:
        user.is_active = changes["is_active"]
        if not user.is_active:
            await revoke_user_tokens(db, user.id)
            disconnect = await _cut_off(db, user, actor_user_id=actor_user_id, ip=ip)

    if changes.get("unlock"):
        user.failed_logins = 0
        user.locked_until = None
    await db.flush()
    return disconnect


async def _cut_off(
    db: AsyncSession, user: User, *, actor_user_id: uuid.UUID | None, ip: str | None
) -> list[uuid.UUID]:
    """Clear the user's presence, record who disabled them, and name the channels to close.

    The devices are NOT marked revoked: the account is disabled, not the hardware, and
    re-activating the user should not leave them re-registering every machine they own.
    """
    device_ids = list(
        await db.scalars(
            select(Device.id).where(Device.user_id == user.id, Device.status == DeviceStatus.ACTIVE)
        )
    )
    if device_ids:
        await db.execute(
            update(Presence)
            .where(Presence.device_id.in_(device_ids))
            .values(connected=False, is_available_host=False, reachable=None)
        )

    details: dict[str, Any] = {"devices_disconnected": len(device_ids)}
    if actor_user_id is not None and actor_user_id != user.id:
        details["actor_user_id"] = str(actor_user_id)
    record_event(
        db,
        SecurityEventType.USER_DEACTIVATED,
        user_id=user.id,
        ip=ip,
        details=details,
    )
    return device_ids


async def get_user_by_email(db: AsyncSession, email: str) -> User | None:
    return await db.scalar(select(User).where(User.email == normalise_email(email)))


async def get_user_by_id(db: AsyncSession, user_id: uuid.UUID) -> User | None:
    return await db.get(User, user_id)


async def create_user(
    db: AsyncSession,
    *,
    email: str,
    password: str,
    display_name: str,
    role: UserRole = UserRole.USER,
) -> User:
    email = normalise_email(email)
    if "@" not in email:
        raise ValidationFailed("email must be a valid email address")
    validate_password(password)
    if not display_name.strip():
        raise ValidationFailed("display_name must not be empty")
    if await get_user_by_email(db, email) is not None:
        raise Conflict("a user with this email already exists")
    user = User(
        email=email,
        password_hash=await hash_password_async(password),
        display_name=display_name.strip(),
        role=UserRole(role),
    )
    db.add(user)
    await db.flush()
    return user
