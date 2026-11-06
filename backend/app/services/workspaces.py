"""Workspaces (ADR-0018). Callers commit, then apply the returned effects and deliver the mail.

A workspace is the boundary nothing is shared across. The rule is enforced in one place - two
accounts can only become contacts inside the same workspace (``app.services.contacts``) - and
every request, offer and session already needs a contact. Moving an account between workspaces
therefore has to take its contacts with it: :func:`_move` removes them (ending any live session
as ``contact_removed``) before the account changes sides.

In ``single`` mode (the self-hosted default) everybody is in the default workspace and the
member-facing endpoints answer ``403 workspaces_disabled``; in ``multi`` mode (the hosted
service) every new account gets a workspace of its own.
"""

import uuid
from collections.abc import Callable
from datetime import timedelta
from typing import Any

from sqlalchemy import delete, func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.errors import ApiError, Conflict, Forbidden, NotFound, ValidationFailed
from app.models import User, Workspace, WorkspaceInvite
from app.models.enums import (
    InviteStatus,
    SecurityEventType,
    SessionEndReason,
    WorkspaceMode,
    WorkspaceRole,
)
from app.schemas.workspaces import (
    AdminWorkspaceOut,
    IncomingWorkspaceInviteOut,
    MemberOut,
    WorkspaceInviteOut,
    WorkspaceOut,
)
from app.services import outbox
from app.services.app_settings import settings_service
from app.services.effects import Effects
from app.services.security_events import record_event

DEFAULT_WORKSPACE_NAME = "Josour"
MANAGERS = (WorkspaceRole.OWNER, WorkspaceRole.ADMIN)


# ---------------------------------------------------------------- mode and placement


async def current_mode(db: AsyncSession) -> WorkspaceMode:
    return (await settings_service.get(db)).workspace_mode


async def ensure_enabled(db: AsyncSession) -> None:
    if await current_mode(db) != WorkspaceMode.MULTI:
        raise ApiError(403, "workspaces_disabled", "This server keeps everybody in one workspace")


async def default_workspace(db: AsyncSession) -> Workspace:
    """The workspace ``single`` mode puts every account in, created the first time it is needed
    (migration 0004 creates it for a server that already had accounts)."""
    row = await db.scalar(select(Workspace).where(Workspace.is_default.is_(True)))
    if row is None:
        row = Workspace(name=DEFAULT_WORKSPACE_NAME, is_default=True, created_at=utcnow())
        db.add(row)
        await db.flush()
    return row


async def create_workspace(db: AsyncSession, name: str) -> Workspace:
    row = Workspace(name=name.strip()[:100] or DEFAULT_WORKSPACE_NAME, created_at=utcnow())
    db.add(row)
    await db.flush()
    return row


async def placement_for_new_account(
    db: AsyncSession, display_name: str
) -> tuple[Workspace, WorkspaceRole]:
    """Where a new account goes: a workspace of its own in ``multi`` mode, the default one in
    ``single`` mode (whose first account is recorded as its owner, for tidiness only)."""
    if await current_mode(db) == WorkspaceMode.MULTI:
        return await create_workspace(db, display_name), WorkspaceRole.OWNER
    workspace = await default_workspace(db)
    has_members = await db.scalar(select(User.id).where(User.workspace_id == workspace.id).limit(1))
    return workspace, WorkspaceRole.MEMBER if has_members else WorkspaceRole.OWNER


async def member_count(db: AsyncSession, workspace_id: uuid.UUID) -> int:
    return int(
        await db.scalar(
            select(func.count())
            .select_from(User)
            .where(User.workspace_id == workspace_id, User.deleted_at.is_(None))
        )
        or 0
    )


async def describe(db: AsyncSession, user: User) -> WorkspaceOut:
    workspace = await db.get(Workspace, user.workspace_id)
    assert workspace is not None  # the foreign key
    return WorkspaceOut(
        id=workspace.id,
        name=workspace.name,
        role=WorkspaceRole(user.workspace_role),
        member_count=await member_count(db, workspace.id),
    )


# ---------------------------------------------------------------- members


def _require_manager(actor: User) -> None:
    if actor.workspace_role not in MANAGERS:
        raise Forbidden("Only the workspace owner or an admin can do this")


async def _member(db: AsyncSession, actor: User, member_id: uuid.UUID) -> User:
    member = await db.get(User, member_id)
    if member is None or member.workspace_id != actor.workspace_id or member.deleted_at:
        raise NotFound("Member not found")
    return member


async def list_members(
    db: AsyncSession, actor: User, *, is_online: Callable[[uuid.UUID], bool]
) -> list[MemberOut]:
    rows = await db.scalars(
        select(User)
        .where(User.workspace_id == actor.workspace_id, User.deleted_at.is_(None))
        .order_by(User.display_name, User.email)
    )
    return [
        MemberOut(
            user_id=row.id,
            display_name=row.display_name,
            email=row.email,
            role=WorkspaceRole(row.workspace_role),
            state=row.state,
            online=is_online(row.id),
            joined_at=row.created_at,
        )
        for row in rows
    ]


async def rename(db: AsyncSession, actor: User, name: str, *, ip: str | None) -> WorkspaceOut:
    _require_manager(actor)
    workspace = await db.get(Workspace, actor.workspace_id)
    assert workspace is not None
    workspace.name = name
    record_event(
        db,
        SecurityEventType.WORKSPACE_RENAMED,
        user_id=actor.id,
        ip=ip,
        details={"workspace_id": str(workspace.id)},
    )
    await db.flush()
    return await describe(db, actor)


async def set_member_role(
    db: AsyncSession, actor: User, member_id: uuid.UUID, role: WorkspaceRole, *, ip: str | None
) -> None:
    """Only the owner changes roles. ``owner`` hands the workspace over and makes the caller an
    admin, so there is always exactly one owner."""
    if actor.workspace_role != WorkspaceRole.OWNER:
        raise Forbidden("Only the workspace owner can change roles")
    member = await _member(db, actor, member_id)
    if member.id == actor.id:
        raise Conflict("hand the workspace to somebody else instead")
    if member.workspace_role == role:
        return
    if role == WorkspaceRole.OWNER:
        if not member.is_active:
            raise Conflict("the new owner's account must be active")
        actor.workspace_role = WorkspaceRole.ADMIN
    member.workspace_role = role
    record_event(
        db,
        SecurityEventType.WORKSPACE_ROLE_CHANGED,
        user_id=member.id,
        ip=ip,
        details={
            "workspace_id": str(member.workspace_id),
            "role": str(role),
            "actor_user_id": str(actor.id),
        },
    )
    await db.flush()


async def remove_member(
    db: AsyncSession, actor: User, member_id: uuid.UUID, *, ip: str | None
) -> Effects:
    """The member keeps the account and gets a workspace of their own; their contacts in this
    one go. The owner can remove anyone but themselves; an admin can remove members only."""
    _require_manager(actor)
    member = await _member(db, actor, member_id)
    if member.id == actor.id:
        raise Conflict("use leave instead")
    if member.workspace_role == WorkspaceRole.OWNER:
        raise Forbidden("The owner cannot be removed")
    if (
        actor.workspace_role == WorkspaceRole.ADMIN
        and member.workspace_role != WorkspaceRole.MEMBER
    ):
        raise Forbidden("Only the owner can remove an admin")
    old = member.workspace_id
    target = await create_workspace(db, member.display_name)
    effects = await _move(db, member, target, WorkspaceRole.OWNER)
    record_event(
        db,
        SecurityEventType.WORKSPACE_MEMBER_REMOVED,
        user_id=member.id,
        ip=ip,
        details={"workspace_id": str(old), "actor_user_id": str(actor.id)},
    )
    return effects


async def leave(db: AsyncSession, user: User, *, ip: str | None) -> Effects:
    if user.workspace_role == WorkspaceRole.OWNER:
        raise ApiError(
            409, "owner_must_transfer", "Hand the workspace to another member before leaving"
        )
    old = user.workspace_id
    target = await create_workspace(db, user.display_name)
    effects = await _move(db, user, target, WorkspaceRole.OWNER)
    record_event(
        db,
        SecurityEventType.WORKSPACE_LEFT,
        user_id=user.id,
        ip=ip,
        details={"workspace_id": str(old)},
    )
    return effects


async def _move(db: AsyncSession, user: User, target: Workspace, role: WorkspaceRole) -> Effects:
    """Take the account out of its workspace and into ``target``.

    Contacts never cross a boundary, so every contact, contact invitation and pending question
    goes first, and a live session ends as ``contact_removed`` - from the other side it is
    exactly that. A workspace left with no accounts at all is deleted."""
    from app.services import contacts as contact_service
    from app.services import sessions as session_service

    effects = Effects()
    live = await session_service.get_live_for_user(db, user.id)
    if live is not None:
        effects.session_events.append(
            await session_service.end_session(db, live, SessionEndReason.CONTACT_REMOVED)
        )
    effects.merge(await contact_service.remove_all_for_user(db, user.id))
    old = user.workspace_id
    user.workspace_id = target.id
    user.workspace_role = role
    await db.flush()
    # Colleagues may have invited the address as a contact before it joined.
    await contact_service.attach_pending_invites(db, user)
    remaining = await db.scalar(select(User.id).where(User.workspace_id == old).limit(1))
    if remaining is None:
        await db.execute(
            delete(Workspace).where(Workspace.id == old, Workspace.is_default.is_(False))
        )
    effects.contacts_changed.add(user.id)
    return effects


# ---------------------------------------------------------------- invitations


def _live(row: WorkspaceInvite) -> bool:
    expires_at = ensure_utc(row.expires_at)
    return row.status == InviteStatus.PENDING and expires_at is not None and expires_at > utcnow()


async def invite(
    db: AsyncSession,
    actor: User,
    email: str,
    role: WorkspaceRole,
    *,
    ttl_days: int,
    ip: str | None,
) -> outbox.Outgoing:
    """``POST /workspace/invites``. Answers the same whatever the address is; an address that is
    already a member gets nothing new."""
    _require_manager(actor)
    if role == WorkspaceRole.ADMIN and actor.workspace_role != WorkspaceRole.OWNER:
        raise Forbidden("Only the owner can invite an admin")
    if role == WorkspaceRole.OWNER:
        raise ValidationFailed("invite as member or admin; hand over ownership afterwards")
    email = email.strip().lower()
    out = outbox.Outgoing()
    member = await db.scalar(
        select(User.id).where(User.email == email, User.workspace_id == actor.workspace_id)
    )
    if member is not None:
        return out
    now = utcnow()
    existing = await db.scalar(
        select(WorkspaceInvite).where(
            WorkspaceInvite.workspace_id == actor.workspace_id,
            WorkspaceInvite.email == email,
            WorkspaceInvite.status == InviteStatus.PENDING,
        )
    )
    if existing is not None:
        if _live(existing):
            existing.role = role
            await db.flush()
            return out
        existing.status = InviteStatus.EXPIRED
        await db.flush()
    row = WorkspaceInvite(
        workspace_id=actor.workspace_id,
        email=email,
        role=role,
        invited_by_user_id=actor.id,
        status=InviteStatus.PENDING,
        created_at=now,
        expires_at=now + timedelta(days=ttl_days),
    )
    db.add(row)
    await db.flush()
    record_event(
        db,
        SecurityEventType.WORKSPACE_INVITE_SENT,
        user_id=actor.id,
        ip=ip,
        details={"invite_id": str(row.id), "workspace_id": str(actor.workspace_id)},
    )
    workspace = await db.get(Workspace, actor.workspace_id)
    assert workspace is not None
    out.emails.append(outbox.workspace_invite_email(email, actor.display_name, workspace.name))
    return out


async def list_sent(db: AsyncSession, actor: User) -> list[WorkspaceInviteOut]:
    _require_manager(actor)
    now = utcnow()
    rows = await db.scalars(
        select(WorkspaceInvite)
        .where(
            WorkspaceInvite.workspace_id == actor.workspace_id,
            # A decline is silent: it shows as pending until it expires.
            WorkspaceInvite.status.in_(
                (InviteStatus.PENDING, InviteStatus.DECLINED, InviteStatus.EXPIRED)
            ),
        )
        .order_by(WorkspaceInvite.created_at.desc())
        .limit(200)
    )
    result = []
    seen: set[str] = set()
    for row in rows:
        if row.email in seen:
            continue  # the newest invitation to an address speaks for it
        seen.add(row.email)
        expires_at = ensure_utc(row.expires_at)
        expired = row.status == InviteStatus.EXPIRED or (
            expires_at is not None and expires_at <= now
        )
        result.append(
            WorkspaceInviteOut(
                id=row.id,
                email=row.email,
                role=WorkspaceRole(row.role),
                status="expired" if expired else "pending",
                created_at=row.created_at,
                expires_at=row.expires_at,
            )
        )
    return result


async def cancel(db: AsyncSession, actor: User, invite_id: uuid.UUID) -> None:
    _require_manager(actor)
    row = await db.get(WorkspaceInvite, invite_id)
    if (
        row is None
        or row.workspace_id != actor.workspace_id
        or row.status not in (InviteStatus.PENDING, InviteStatus.DECLINED, InviteStatus.EXPIRED)
    ):
        raise NotFound("Invitation not found")
    row.status = InviteStatus.CANCELLED
    row.responded_at = utcnow()
    await db.flush()


async def incoming(db: AsyncSession, user: User) -> list[IncomingWorkspaceInviteOut]:
    now = utcnow()
    inviter = User.__table__.alias("inviter")
    rows = await db.execute(
        select(WorkspaceInvite, Workspace.name, inviter.c.display_name)
        .join(Workspace, Workspace.id == WorkspaceInvite.workspace_id)
        .outerjoin(inviter, inviter.c.id == WorkspaceInvite.invited_by_user_id)
        .where(
            WorkspaceInvite.email == user.email,
            WorkspaceInvite.status == InviteStatus.PENDING,
            WorkspaceInvite.expires_at > now,
            WorkspaceInvite.workspace_id != user.workspace_id,
        )
        .order_by(WorkspaceInvite.created_at.desc())
    )
    return [
        IncomingWorkspaceInviteOut(
            id=row.id,
            workspace_id=row.workspace_id,
            workspace_name=name,
            role=WorkspaceRole(row.role),
            from_display_name=inviter_name,
            created_at=row.created_at,
            expires_at=row.expires_at,
        )
        for row, name, inviter_name in rows
    ]


async def _incoming_row(db: AsyncSession, user: User, invite_id: uuid.UUID) -> WorkspaceInvite:
    row = await db.get(WorkspaceInvite, invite_id)
    if row is None or row.email != user.email or not _live(row):
        raise NotFound("Invitation not found")
    return row


async def accept(db: AsyncSession, user: User, invite_id: uuid.UUID, *, ip: str | None) -> Effects:
    """Join the inviting workspace. An owner whose workspace has other members must hand it over
    first; a workspace left empty is deleted."""
    row = await _incoming_row(db, user, invite_id)
    if row.workspace_id == user.workspace_id:
        raise Conflict("you are already a member of this workspace")
    if user.workspace_role == WorkspaceRole.OWNER and await member_count(db, user.workspace_id) > 1:
        raise ApiError(
            409, "owner_must_transfer", "Hand your workspace to another member before joining"
        )
    target = await db.get(Workspace, row.workspace_id)
    assert target is not None
    old = user.workspace_id
    effects = await _move(db, user, target, WorkspaceRole(row.role))
    row.status = InviteStatus.ACCEPTED
    row.responded_at = utcnow()
    record_event(
        db,
        SecurityEventType.WORKSPACE_JOINED,
        user_id=user.id,
        ip=ip,
        details={"workspace_id": str(target.id), "left_workspace_id": str(old)},
    )
    await db.flush()
    return effects


async def decline(db: AsyncSession, user: User, invite_id: uuid.UUID) -> None:
    row = await _incoming_row(db, user, invite_id)
    row.status = InviteStatus.DECLINED
    row.responded_at = utcnow()
    await db.flush()


# ---------------------------------------------------------------- operator


async def list_for_admin(
    db: AsyncSession, *, q: str | None = None, limit: int = 100, offset: int = 0
) -> list[AdminWorkspaceOut]:
    counts = (
        select(User.workspace_id, func.count().label("n"))
        .where(User.deleted_at.is_(None))
        .group_by(User.workspace_id)
        .subquery()
    )
    owner = User.__table__.alias("owner")
    stmt: Any = (
        select(Workspace, counts.c.n, owner.c.id, owner.c.email)
        .outerjoin(counts, counts.c.workspace_id == Workspace.id)
        .outerjoin(
            owner,
            (owner.c.workspace_id == Workspace.id)
            & (owner.c.workspace_role == WorkspaceRole.OWNER.value)
            & owner.c.deleted_at.is_(None),
        )
        .order_by(Workspace.created_at.desc())
        .limit(limit)
        .offset(offset)
    )
    if q and q.strip():
        stmt = stmt.where(Workspace.name.ilike(f"%{q.strip()}%"))
    return [
        AdminWorkspaceOut(
            id=row.id,
            name=row.name,
            is_default=row.is_default,
            member_count=int(n or 0),
            owner_user_id=owner_id,
            owner_email=owner_email,
            created_at=row.created_at,
        )
        for row, n, owner_id, owner_email in await db.execute(stmt)
    ]
