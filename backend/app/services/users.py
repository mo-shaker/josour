"""User lookup, creation and admin updates. Callers commit."""

import uuid

from sqlalchemy import or_, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.errors import Conflict, ValidationFailed
from app.core.security import hash_password_async
from app.models import User
from app.models.enums import UserRole
from app.schemas.admin import AdminUserPatch
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


async def update_user(db: AsyncSession, user: User, patch: AdminUserPatch) -> User:
    """``PATCH /admin/users/{id}``. Deactivating or changing the password revokes every live
    refresh token of the user so existing devices must log in again."""
    changes = patch.model_dump(exclude_unset=True)
    if changes.get("display_name") is not None:
        user.display_name = changes["display_name"]
    if changes.get("password") is not None:
        validate_password(changes["password"])
        user.password_hash = await hash_password_async(changes["password"])
        await revoke_user_tokens(db, user.id)
    if changes.get("is_active") is not None and changes["is_active"] != user.is_active:
        user.is_active = changes["is_active"]
        if not user.is_active:
            await revoke_user_tokens(db, user.id)
    if changes.get("unlock"):
        user.failed_logins = 0
        user.locked_until = None
    await db.flush()
    return user


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
