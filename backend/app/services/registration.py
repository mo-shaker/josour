"""Self-registration from the app (ADR-0017). The caller commits.

In ``approval`` mode - the default - the account is created ``pending``: it exists, its email is
taken, but it cannot sign in until an administrator approves it. The answer to the caller is the
same whether the email was free or already taken, so registering cannot be used to find out who
has an account; the attempt on a taken address is recorded for the administrator instead.

``open`` mode creates an active account at once. It cannot hide a taken address - the person
would otherwise sign in to somebody else's account or be told nothing - so it says so, and that
is documented as the price of the mode.
"""

import enum

from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.errors import ApiError, Conflict
from app.core.security import hash_password_async
from app.models import User
from app.models.enums import ApprovalStatus, RegisteredVia, RegistrationMode, SecurityEventType
from app.services import contacts as contact_service
from app.services.app_settings import settings_service
from app.services.security_events import record_event
from app.services.users import create_user, get_user_by_email


class RegistrationOutcome(enum.StrEnum):
    PENDING = "pending"
    ACTIVE = "active"


async def current_mode(db: AsyncSession) -> RegistrationMode:
    return (await settings_service.get(db)).registration_mode


async def register(
    db: AsyncSession,
    *,
    email: str,
    password: str,
    display_name: str,
    ip: str | None,
    max_pending: int,
) -> RegistrationOutcome:
    mode = await current_mode(db)
    if mode == RegistrationMode.CLOSED:
        raise ApiError(403, "registration_closed", "Registration is closed on this server")

    existing = await get_user_by_email(db, email)
    if mode == RegistrationMode.APPROVAL:
        pending = await db.scalar(
            select(func.count())
            .select_from(User)
            .where(User.approval_status == ApprovalStatus.PENDING, User.deleted_at.is_(None))
        )
        if (pending or 0) >= max_pending:
            raise ApiError(
                429,
                "registration_unavailable",
                "Too many registrations are waiting for approval; try again later",
                headers={"Retry-After": "3600"},
            )
        if existing is not None:
            # The same work a real registration does, so the answer takes the same time.
            await hash_password_async(password)
            record_event(
                db,
                SecurityEventType.REGISTRATION_DUPLICATE,
                user_id=existing.id,
                ip=ip,
                details={"mode": mode.value},
            )
            return RegistrationOutcome.PENDING
        user = await create_user(
            db,
            email=email,
            password=password,
            display_name=display_name,
            approval_status=ApprovalStatus.PENDING,
            registered_via=RegisteredVia.SELF,
        )
        outcome = RegistrationOutcome.PENDING
    else:
        if existing is not None:
            raise Conflict("a user with this email already exists")
        user = await create_user(
            db,
            email=email,
            password=password,
            display_name=display_name,
            registered_via=RegisteredVia.SELF,
        )
        await contact_service.attach_pending_invites(db, user)
        outcome = RegistrationOutcome.ACTIVE

    record_event(
        db,
        SecurityEventType.REGISTRATION_SUBMITTED,
        user_id=user.id,
        ip=ip,
        details={"mode": mode.value},
    )
    return outcome
