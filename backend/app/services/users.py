"""User lookup and creation. Callers commit."""

import uuid

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.errors import Conflict, ValidationFailed
from app.core.security import hash_password
from app.models import User
from app.models.enums import UserRole


def normalise_email(email: str) -> str:
    return email.strip().lower()


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
    if len(password) < 8:
        raise ValidationFailed("password must be at least 8 characters")
    if not display_name.strip():
        raise ValidationFailed("display_name must not be empty")
    if await get_user_by_email(db, email) is not None:
        raise Conflict("a user with this email already exists")
    user = User(
        email=email,
        password_hash=hash_password(password),
        display_name=display_name.strip(),
        role=UserRole(role),
    )
    db.add(user)
    await db.flush()
    return user
