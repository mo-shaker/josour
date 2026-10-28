"""User lookup, creation, approval and admin updates. Callers commit."""

import enum
import secrets
import uuid
from typing import Any

from sqlalchemy import func, or_, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.core.errors import Conflict, ValidationFailed
from app.core.security import hash_password_async
from app.models import Device, Presence, User
from app.models.enums import (
    ApprovalStatus,
    DeviceStatus,
    RegisteredVia,
    SecurityEventType,
    SessionEndReason,
    UserRole,
)
from app.schemas.admin import AdminUserPatch
from app.services import admin_web_sessions
from app.services.effects import Effects
from app.services.security_events import record_event
from app.services.tokens import revoke_user_tokens

MIN_PASSWORD_LENGTH = 8
DELETED_EMAIL_DOMAIN = "deleted.invalid"
"""RFC 2606 reserves ``.invalid``: a deleted account's address can never be anybody's."""
DELETED_DISPLAY_NAME = "Deleted user"


class UserState(enum.StrEnum):
    """One word for where an account stands, derived from its columns (admin list filter)."""

    ACTIVE = "active"
    DISABLED = "disabled"
    PENDING = "pending"
    REJECTED = "rejected"
    DELETED = "deleted"


def user_state(user: User) -> UserState:
    return UserState(user.state)


def normalise_email(email: str) -> str:
    return email.strip().lower()


def validate_password(password: str) -> None:
    if len(password) < MIN_PASSWORD_LENGTH:
        raise ValidationFailed(f"password must be at least {MIN_PASSWORD_LENGTH} characters")


def _escape_like(value: str) -> str:
    return value.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")


def _state_filter(state: UserState) -> Any:
    match state:
        case UserState.DELETED:
            return User.deleted_at.is_not(None)
        case UserState.PENDING:
            return (User.approval_status == ApprovalStatus.PENDING) & User.deleted_at.is_(None)
        case UserState.REJECTED:
            return (User.approval_status == ApprovalStatus.REJECTED) & User.deleted_at.is_(None)
        case UserState.ACTIVE:
            return User.is_active.is_(True) & User.deleted_at.is_(None)
        case UserState.DISABLED:
            return (
                User.is_active.is_(False)
                & (User.approval_status == ApprovalStatus.APPROVED)
                & User.deleted_at.is_(None)
            )


async def list_users(
    db: AsyncSession,
    *,
    q: str | None = None,
    state: UserState | None = None,
    role: UserRole | None = None,
    limit: int = 100,
    offset: int = 0,
) -> list[User]:
    """Newest first; ``q`` is a case-insensitive substring match on email or display_name."""
    stmt = select(User).order_by(User.created_at.desc(), User.email).limit(limit).offset(offset)
    if q and q.strip():
        pattern = f"%{_escape_like(q.strip())}%"
        stmt = stmt.where(
            or_(
                User.email.ilike(pattern, escape="\\"),
                User.display_name.ilike(pattern, escape="\\"),
            )
        )
    if state is not None:
        stmt = stmt.where(_state_filter(state))
    if role is not None:
        stmt = stmt.where(User.role == role)
    return list(await db.scalars(stmt))


async def count_pending(db: AsyncSession) -> int:
    """Registrations waiting for an administrator (the dashboard's badge)."""
    return int(
        await db.scalar(
            select(func.count()).select_from(User).where(_state_filter(UserState.PENDING))
        )
        or 0
    )


async def count_by_state(db: AsyncSession) -> dict[UserState, int]:
    """Totals for the dashboard overview, one query per state (a handful of rows to count)."""
    counts: dict[UserState, int] = {}
    for state in UserState:
        counts[state] = int(
            await db.scalar(select(func.count()).select_from(User).where(_state_filter(state))) or 0
        )
    return counts


async def update_user(
    db: AsyncSession,
    user: User,
    patch: AdminUserPatch,
    *,
    actor_user_id: uuid.UUID | None = None,
    ip: str | None = None,
    via: str | None = None,
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

    A pending or rejected account cannot be activated here - that is what approving it is for -
    and a deleted one cannot be changed at all.

    Every change is on the record: ``user_updated`` (email, role, password, reactivation,
    unlock) or ``user_deactivated``, naming the administrator and, from the dashboard,
    ``via: "web"``.
    """
    if user.deleted_at is not None:
        raise Conflict("a deleted account cannot be changed")
    changes = patch.model_dump(exclude_unset=True)
    audit: dict[str, Any] = {}
    if changes.get("display_name") is not None:
        user.display_name = changes["display_name"]
    if changes.get("email") is not None and changes["email"] != user.email:
        await change_email(db, user, changes["email"])
        audit["email_changed"] = True
    if changes.get("role") is not None and changes["role"] != user.role:
        await set_role(db, user, UserRole(changes["role"]), actor_user_id=actor_user_id)
        audit["role"] = str(changes["role"])
    if changes.get("password") is not None:
        validate_password(changes["password"])
        user.password_hash = await hash_password_async(changes["password"])
        await revoke_user_tokens(db, user.id)
        await admin_web_sessions.revoke_all_for_user(db, user.id)
        audit["password_set"] = True

    disconnect: list[uuid.UUID] = []
    admin_lost = False
    if changes.get("is_active") is not None and changes["is_active"] != user.is_active:
        if changes["is_active"] and user.approval_status != ApprovalStatus.APPROVED:
            raise Conflict("approve the account before activating it")
        if not changes["is_active"]:
            await _assert_not_last_admin(db, user, actor_user_id=actor_user_id)
            admin_lost = user.role == UserRole.ADMIN
        user.is_active = changes["is_active"]
        if user.is_active:
            audit["activated"] = True
        else:
            await revoke_user_tokens(db, user.id)
            await admin_web_sessions.revoke_all_for_user(db, user.id)
            disconnect = await _cut_off(db, user, actor_user_id=actor_user_id, ip=ip, via=via)

    if changes.get("unlock"):
        user.failed_logins = 0
        user.locked_until = None
        audit["unlocked"] = True
    if audit:
        details: dict[str, Any] = dict(audit)
        if actor_user_id is not None:
            details["actor_user_id"] = str(actor_user_id)
        if via is not None:
            details["via"] = via
        record_event(db, SecurityEventType.USER_UPDATED, user_id=user.id, ip=ip, details=details)
    await db.flush()
    if admin_lost or audit.get("role") == UserRole.USER.value:
        await _assert_an_admin_remains(db)
    return disconnect


async def _cut_off(
    db: AsyncSession,
    user: User,
    *,
    actor_user_id: uuid.UUID | None,
    ip: str | None,
    via: str | None = None,
) -> list[uuid.UUID]:
    """Clear the user's presence, record who disabled them, and name the channels to close.

    The devices are NOT marked revoked: the account is disabled, not the hardware, and
    re-activating the user should not leave them re-registering every machine they own.
    """
    device_ids = await _clear_presence(db, user.id)

    details: dict[str, Any] = {"devices_disconnected": len(device_ids)}
    if actor_user_id is not None and actor_user_id != user.id:
        details["actor_user_id"] = str(actor_user_id)
    if via is not None:
        details["via"] = via
    record_event(
        db,
        SecurityEventType.USER_DEACTIVATED,
        user_id=user.id,
        ip=ip,
        details=details,
    )
    return device_ids


async def _clear_presence(db: AsyncSession, user_id: uuid.UUID) -> list[uuid.UUID]:
    """Mark every active device of the user offline; returns their ids."""
    device_ids = list(
        await db.scalars(
            select(Device.id).where(Device.user_id == user_id, Device.status == DeviceStatus.ACTIVE)
        )
    )
    if device_ids:
        await db.execute(
            update(Presence)
            .where(Presence.device_id.in_(device_ids))
            .values(connected=False, is_available_host=False, reachable=None)
        )
    return device_ids


async def _assert_not_last_admin(
    db: AsyncSession, user: User, *, actor_user_id: uuid.UUID | None
) -> None:
    """Nobody may disable, demote or delete themselves, nor remove the last working admin:
    either would leave a deployment that only the shell can recover."""
    if actor_user_id is not None and actor_user_id == user.id:
        raise Conflict("you cannot do this to your own account")
    if user.role != UserRole.ADMIN or not user.is_active:
        return
    # Locks every active administrator row (PostgreSQL; SQLite ignores it), in one order, so two
    # administrators removing each other at the same moment are serialised here and the second
    # one sees the first one's change. :func:`_assert_an_admin_remains` re-checks after the
    # write, which is what serialises SQLite (its write lock waits for the other commit).
    others = [admin for admin in await _active_admin_ids(db, lock=True) if admin != user.id]
    if not others:
        raise Conflict("this is the last active administrator")


async def _active_admin_ids(db: AsyncSession, *, lock: bool) -> list[uuid.UUID]:
    stmt = (
        select(User.id)
        .where(
            User.role == UserRole.ADMIN,
            User.is_active.is_(True),
            User.deleted_at.is_(None),
        )
        .order_by(User.id)
    )
    if lock:
        stmt = stmt.with_for_update()
    return list(await db.scalars(stmt))


async def _assert_an_admin_remains(db: AsyncSession) -> None:
    """After the write that disabled, demoted or deleted an administrator (flushed): if a
    concurrent change took the other one meanwhile, refuse rather than commit no admin at all."""
    if not await _active_admin_ids(db, lock=False):
        raise Conflict("this is the last active administrator")


async def set_role(
    db: AsyncSession, user: User, role: UserRole, *, actor_user_id: uuid.UUID | None
) -> None:
    if role == user.role:
        return
    if role != UserRole.ADMIN:
        await _assert_not_last_admin(db, user, actor_user_id=actor_user_id)
        await admin_web_sessions.revoke_all_for_user(db, user.id)
    user.role = role


async def change_email(db: AsyncSession, user: User, email: str) -> None:
    """A new sign-in address. Invitations already sent to the new address now reach this
    account; ones that matched the old address and were already delivered stay delivered."""
    from app.services import contacts as contact_service

    email = normalise_email(email)
    if "@" not in email:
        raise ValidationFailed("email must be a valid email address")
    existing = await get_user_by_email(db, email)
    if existing is not None and existing.id != user.id:
        raise Conflict("a user with this email already exists")
    user.email = email
    await db.flush()
    if user.approval_status == ApprovalStatus.APPROVED:
        await contact_service.attach_pending_invites(db, user)


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
    approval_status: ApprovalStatus = ApprovalStatus.APPROVED,
    registered_via: RegisteredVia = RegisteredVia.ADMIN,
) -> User:
    """Approved accounts start active; pending and rejected ones never do (the CHECK)."""
    email = normalise_email(email)
    if "@" not in email:
        raise ValidationFailed("email must be a valid email address")
    validate_password(password)
    if not display_name.strip():
        raise ValidationFailed("display_name must not be empty")
    if await get_user_by_email(db, email) is not None:
        raise Conflict("a user with this email already exists")
    approved = approval_status == ApprovalStatus.APPROVED
    user = User(
        email=email,
        password_hash=await hash_password_async(password),
        display_name=display_name.strip(),
        role=UserRole(role),
        approval_status=approval_status,
        registered_via=registered_via,
        is_active=approved,
        approved_at=utcnow() if approved else None,
    )
    db.add(user)
    await db.flush()
    return user


def record_created(
    db: AsyncSession,
    user: User,
    *,
    actor_user_id: uuid.UUID | None,
    ip: str | None,
    via: str | None = None,
) -> None:
    """``user_created`` for an account an administrator (or the shell) made. Not part of
    :func:`create_user`, which self-registration uses too and audits as its own event."""
    record_event(
        db,
        SecurityEventType.USER_CREATED,
        user_id=user.id,
        ip=ip,
        details={"role": str(user.role), **_actor_details(actor_user_id, via)},
    )


async def approve_user(
    db: AsyncSession,
    user: User,
    *,
    actor_user_id: uuid.UUID | None,
    ip: str | None,
    via: str | None = None,
) -> None:
    """Let a self-registered account in. A rejected account may be approved after all - an
    administrator changing their mind is not an error - but an approved or deleted one may not."""
    from app.services import contacts as contact_service

    if user.deleted_at is not None:
        raise Conflict("a deleted account cannot be approved")
    if user.approval_status == ApprovalStatus.APPROVED:
        raise Conflict("this account is already approved")
    now = utcnow()
    user.approval_status = ApprovalStatus.APPROVED
    user.is_active = True
    user.approved_at = now
    user.approved_by = actor_user_id
    user.rejected_at = None
    await db.flush()
    await contact_service.attach_pending_invites(db, user)
    record_event(
        db,
        SecurityEventType.USER_APPROVED,
        user_id=user.id,
        ip=ip,
        details=_actor_details(actor_user_id, via),
    )


async def reject_user(
    db: AsyncSession,
    user: User,
    *,
    actor_user_id: uuid.UUID | None,
    ip: str | None,
    via: str | None = None,
) -> None:
    """Turn a pending registration down. The account stays (so the same email cannot simply
    register again) but can never sign in; an administrator can still approve it later."""
    if user.deleted_at is not None:
        raise Conflict("a deleted account cannot be rejected")
    if user.approval_status != ApprovalStatus.PENDING:
        raise Conflict("only a pending account can be rejected")
    user.approval_status = ApprovalStatus.REJECTED
    user.is_active = False
    user.rejected_at = utcnow()
    await db.flush()
    record_event(
        db,
        SecurityEventType.USER_REJECTED,
        user_id=user.id,
        ip=ip,
        details=_actor_details(actor_user_id, via),
    )


async def delete_user(
    db: AsyncSession,
    user: User,
    *,
    actor_user_id: uuid.UUID | None,
    ip: str | None,
    via: str | None = None,
) -> Effects:
    """Soft delete (ADR-0017). The row stays, because session history of *other* people points
    at it and a hard delete would cascade through theirs; everything that identifies the person
    or lets them back in goes:

    - email, display name and password are replaced (the new password hash is of random bytes
      nobody knows, so a sign-in costs the same time as any other wrong password);
    - every device is revoked and every refresh token with it, and presence is cleared;
    - contacts, invitations and trust grants in both directions are removed;
    - any live session is ended as ``admin_terminated``.
    """
    from app.services import contacts as contact_service
    from app.services import devices as device_service
    from app.services import sessions as session_service

    if user.deleted_at is not None:
        raise Conflict("this account is already deleted")
    await _assert_not_last_admin(db, user, actor_user_id=actor_user_id)
    was_admin = user.role == UserRole.ADMIN and user.is_active

    change = Effects()
    live = await session_service.get_live_for_user(db, user.id)
    if live is not None:
        change.session_events.append(
            await session_service.end_session(db, live, SessionEndReason.ADMIN_TERMINATED)
        )
    change.merge(await contact_service.remove_all_for_user(db, user.id))

    devices = list(await db.scalars(select(Device).where(Device.user_id == user.id)))
    for device in devices:
        await device_service.revoke_device(
            db,
            device,
            by="admin",
            actor_user_id=actor_user_id,
            ip=ip,
            reason={"cause": "user_deleted"},
            via=via,
        )
    change.disconnect = [device.id for device in devices]
    await revoke_user_tokens(db, user.id)
    await admin_web_sessions.revoke_all_for_user(db, user.id)

    now = utcnow()
    user.email = f"deleted-{user.id}@{DELETED_EMAIL_DOMAIN}"
    user.display_name = DELETED_DISPLAY_NAME
    user.password_hash = await hash_password_async(secrets.token_urlsafe(32))
    user.is_active = False
    user.deleted_at = now
    user.failed_logins = 0
    user.locked_until = None
    user.totp_secret = None
    user.totp_enabled_at = None
    await db.flush()
    if was_admin:
        await _assert_an_admin_remains(db)
    record_event(
        db,
        SecurityEventType.USER_DELETED,
        user_id=user.id,
        ip=ip,
        details={**_actor_details(actor_user_id, via), "devices_revoked": len(devices)},
    )
    return change


def _actor_details(actor_user_id: uuid.UUID | None, via: str | None = None) -> dict[str, Any]:
    """Who did it: an administrator (and from where, ``via: "web"`` for the dashboard), or the
    shell (``via: "cli"``)."""
    if actor_user_id is None:
        return {"via": via or "cli"}
    details: dict[str, Any] = {"actor_user_id": str(actor_user_id)}
    if via is not None:
        details["via"] = via
    return details
