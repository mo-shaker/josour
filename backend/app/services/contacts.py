"""Contacts, invitations and trust (ADR-0014). Callers commit, then apply the returned effects.

Who can share a connection with whom is decided here and nowhere else:

* two users are contacts once one invited the other **by email** and the other accepted;
* only contacts see each other in the host list, and only contacts can ask for or offer a
  connection (``app.services.hosts`` and ``app.services.requests`` both ask this module);
* each side owns one switch about its *own* connection - "this contact may use it without asking
  me" - which is the ``auto_accept`` column of the user's own row.

Invitations never reveal whether an address has an account. Inviting an unknown address and a
known one return the same answer and leave the same thing in the inviter's outgoing list; a
decline is silent, so it is indistinguishable from an address nobody uses.
"""

import uuid
from collections.abc import Callable, Iterable
from datetime import timedelta
from typing import Any

from sqlalchemy import and_, delete, or_, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc, utcnow
from app.core.errors import NotFound, ValidationFailed
from app.models import ConnectionRequest, Contact, ContactInvite, Session, User
from app.models.enums import (
    NON_ENDED_SESSION_STATUSES,
    ApprovalStatus,
    InviteStatus,
    RequestStatus,
    SecurityEventType,
    SessionEndReason,
)
from app.schemas.contacts import (
    AdminContactOut,
    AdminInviteOut,
    ContactOut,
    IncomingInviteOut,
    InvitesOut,
    OutgoingInviteOut,
)
from app.services.effects import Effects, SettledRequest
from app.services.security_events import record_event

OUTGOING_LIMIT = 50


# ---------------------------------------------------------------- queries


async def get_link(db: AsyncSession, owner_id: uuid.UUID, contact_id: uuid.UUID) -> Contact | None:
    """The owner's row about this contact, or ``None`` when they are not contacts."""
    return await db.get(Contact, (owner_id, contact_id))


async def are_contacts(db: AsyncSession, a: uuid.UUID, b: uuid.UUID) -> bool:
    return await get_link(db, a, b) is not None


async def trusts(db: AsyncSession, owner_id: uuid.UUID, contact_id: uuid.UUID) -> bool:
    """Whether ``owner`` lets ``contact`` use the owner's connection without asking."""
    link = await get_link(db, owner_id, contact_id)
    return link is not None and link.auto_accept


async def contact_ids(db: AsyncSession, user_id: uuid.UUID) -> list[uuid.UUID]:
    return list(await db.scalars(select(Contact.contact_user_id).where(Contact.user_id == user_id)))


async def adjacency(
    db: AsyncSession, viewer_ids: Iterable[uuid.UUID], owner_ids: Iterable[uuid.UUID]
) -> dict[uuid.UUID, set[uuid.UUID]]:
    """For every viewer, which of ``owner_ids`` are their contacts - one query for a whole
    ``hosts.update`` fan-out instead of one per connected user."""
    viewers, owners = set(viewer_ids), set(owner_ids)
    result: dict[uuid.UUID, set[uuid.UUID]] = {}
    if not viewers or not owners:
        return result
    rows = await db.execute(
        select(Contact.contact_user_id, Contact.user_id).where(
            Contact.contact_user_id.in_(viewers), Contact.user_id.in_(owners)
        )
    )
    for viewer, owner in rows:
        result.setdefault(viewer, set()).add(owner)
    return result


async def list_contacts(
    db: AsyncSession, user_id: uuid.UUID, *, is_online: Callable[[uuid.UUID], bool]
) -> list[ContactOut]:
    """``GET /contacts``: mine and theirs joined, so each entry carries both trust switches."""
    theirs = Contact.__table__.alias("theirs")
    rows = await db.execute(
        select(Contact, User.display_name, User.email, theirs.c.auto_accept)
        .join(User, User.id == Contact.contact_user_id)
        .outerjoin(
            theirs,
            and_(
                theirs.c.user_id == Contact.contact_user_id,
                theirs.c.contact_user_id == Contact.user_id,
            ),
        )
        .where(Contact.user_id == user_id, User.deleted_at.is_(None))
        .order_by(User.display_name, User.email)
    )
    return [
        ContactOut(
            user_id=link.contact_user_id,
            display_name=name,
            email=email,
            online=is_online(link.contact_user_id),
            i_trust=link.auto_accept,
            trusts_me=bool(trusts_me),
            since=link.created_at,
        )
        for link, name, email, trusts_me in rows
    ]


async def get_contact(
    db: AsyncSession,
    user_id: uuid.UUID,
    contact_id: uuid.UUID,
    *,
    is_online: Callable[[uuid.UUID], bool],
) -> ContactOut:
    for contact in await list_contacts(db, user_id, is_online=is_online):
        if contact.user_id == contact_id:
            return contact
    raise NotFound("Contact not found")


async def list_for_admin(db: AsyncSession, user_id: uuid.UUID) -> list[AdminContactOut]:
    return [
        AdminContactOut(
            user_id=user_id,
            contact_user_id=contact.user_id,
            contact_display_name=contact.display_name,
            contact_email=contact.email,
            auto_accept=contact.i_trust,
            trusted_by_contact=contact.trusts_me,
            created_at=contact.since,
        )
        for contact in await list_contacts(db, user_id, is_online=lambda _: False)
    ]


# ---------------------------------------------------------------- invitations


def _live(invite: ContactInvite) -> bool:
    expires_at = ensure_utc(invite.expires_at)
    return (
        invite.status == InviteStatus.PENDING and expires_at is not None and expires_at > utcnow()
    )


async def _invitable_user(db: AsyncSession, email: str) -> User | None:
    """The account an invitation to ``email`` is delivered to, if there is one that may receive
    it: approved and not deleted. A pending account gets it on approval."""
    return await db.scalar(
        select(User).where(
            User.email == email,
            User.approval_status == ApprovalStatus.APPROVED,
            User.deleted_at.is_(None),
        )
    )


async def _connect(db: AsyncSession, a: uuid.UUID, b: uuid.UUID) -> None:
    """Write both directions of the relationship (idempotent)."""
    for owner, contact in ((a, b), (b, a)):
        if await get_link(db, owner, contact) is None:
            db.add(Contact(user_id=owner, contact_user_id=contact, created_at=utcnow()))
    await db.flush()


async def invite(
    db: AsyncSession, inviter: User, email: str, *, ttl_days: int, ip: str | None
) -> Effects:
    """``POST /contacts/invites``. The caller always answers the same thing, whatever happened
    here; that is the property, so this returns nothing the caller could leak."""
    email = email.strip().lower()
    if email == inviter.email:
        raise ValidationFailed("you cannot invite yourself")
    effects = Effects(contacts_changed={inviter.id})
    now = utcnow()
    target = await _invitable_user(db, email)
    if target is not None:
        if await are_contacts(db, inviter.id, target.id):
            return effects
        reverse = await db.scalar(
            select(ContactInvite).where(
                ContactInvite.inviter_user_id == target.id,
                ContactInvite.status == InviteStatus.PENDING,
                ContactInvite.expires_at > now,
                or_(
                    ContactInvite.invitee_user_id == inviter.id,
                    ContactInvite.invitee_email == inviter.email,
                ),
            )
        )
        if reverse is not None:
            # They already asked me: asking them back is the same as accepting.
            await _accept(db, reverse, invitee=inviter, ip=ip)
            effects.contacts_changed |= {inviter.id, target.id}
            return effects

    existing = await db.scalar(
        select(ContactInvite).where(
            ContactInvite.inviter_user_id == inviter.id,
            ContactInvite.invitee_email == email,
            ContactInvite.status == InviteStatus.PENDING,
        )
    )
    if existing is not None:
        if _live(existing):
            return effects
        existing.status = InviteStatus.EXPIRED
        await db.flush()  # frees the partial unique index for the new row

    row = ContactInvite(
        inviter_user_id=inviter.id,
        invitee_email=email,
        invitee_user_id=target.id if target is not None else None,
        status=InviteStatus.PENDING,
        created_at=now,
        expires_at=now + timedelta(days=ttl_days),
    )
    db.add(row)
    await db.flush()
    record_event(
        db,
        SecurityEventType.CONTACT_INVITE_SENT,
        user_id=inviter.id,
        ip=ip,
        details={"invite_id": str(row.id)},
    )
    if target is not None:
        effects.contacts_changed.add(target.id)
    return effects


async def _accept(
    db: AsyncSession, invite_row: ContactInvite, *, invitee: User, ip: str | None
) -> None:
    now = utcnow()
    await _connect(db, invite_row.inviter_user_id, invitee.id)
    invite_row.status = InviteStatus.ACCEPTED
    invite_row.invitee_user_id = invitee.id
    invite_row.responded_at = now
    # Anything pending the other way round is answered by the same act.
    others = await db.scalars(
        select(ContactInvite).where(
            ContactInvite.inviter_user_id == invitee.id,
            ContactInvite.invitee_user_id == invite_row.inviter_user_id,
            ContactInvite.status == InviteStatus.PENDING,
        )
    )
    for other in others:
        other.status = InviteStatus.ACCEPTED
        other.responded_at = now
    record_event(
        db,
        SecurityEventType.CONTACT_ADDED,
        user_id=invitee.id,
        ip=ip,
        details={"contact_user_id": str(invite_row.inviter_user_id)},
    )
    await db.flush()


async def _incoming(db: AsyncSession, user: User, invite_id: uuid.UUID) -> ContactInvite:
    row = await db.get(ContactInvite, invite_id)
    if row is None or row.invitee_user_id != user.id or not _live(row):
        raise NotFound("Invitation not found")
    return row


async def accept_invite(
    db: AsyncSession, user: User, invite_id: uuid.UUID, *, ip: str | None
) -> tuple[uuid.UUID, Effects]:
    """Returns the new contact's user id."""
    row = await _incoming(db, user, invite_id)
    await _accept(db, row, invitee=user, ip=ip)
    return row.inviter_user_id, Effects(contacts_changed={user.id, row.inviter_user_id})


async def decline_invite(db: AsyncSession, user: User, invite_id: uuid.UUID) -> Effects:
    """Silent: the inviter keeps seeing ``pending`` until the invitation expires."""
    row = await _incoming(db, user, invite_id)
    row.status = InviteStatus.DECLINED
    row.responded_at = utcnow()
    await db.flush()
    return Effects(contacts_changed={user.id})


async def cancel_invite(db: AsyncSession, user: User, invite_id: uuid.UUID) -> Effects:
    """The inviter withdraws (or clears an expired one from their list)."""
    row = await db.get(ContactInvite, invite_id)
    if (
        row is None
        or row.inviter_user_id != user.id
        or row.status not in (InviteStatus.PENDING, InviteStatus.DECLINED, InviteStatus.EXPIRED)
    ):
        raise NotFound("Invitation not found")
    was_live = _live(row)
    row.status = InviteStatus.CANCELLED
    row.responded_at = utcnow()
    await db.flush()
    effects = Effects(contacts_changed={user.id})
    if was_live and row.invitee_user_id is not None:
        effects.contacts_changed.add(row.invitee_user_id)
    return effects


async def list_invites(db: AsyncSession, user: User) -> InvitesOut:
    now = utcnow()
    incoming_rows = await db.execute(
        select(ContactInvite, User.display_name, User.email)
        .join(User, User.id == ContactInvite.inviter_user_id)
        .where(
            ContactInvite.invitee_user_id == user.id,
            ContactInvite.status == InviteStatus.PENDING,
            ContactInvite.expires_at > now,
            User.deleted_at.is_(None),
        )
        .order_by(ContactInvite.created_at.desc())
    )
    incoming = [
        IncomingInviteOut(
            id=row.id,
            from_user_id=row.inviter_user_id,
            from_display_name=name,
            from_email=email,
            created_at=row.created_at,
            expires_at=row.expires_at,
        )
        for row, name, email in incoming_rows
    ]

    outgoing_rows = await db.scalars(
        select(ContactInvite)
        .where(
            ContactInvite.inviter_user_id == user.id,
            ContactInvite.status.in_(
                (InviteStatus.PENDING, InviteStatus.DECLINED, InviteStatus.EXPIRED)
            ),
        )
        .order_by(ContactInvite.created_at.desc())
        .limit(OUTGOING_LIMIT * 2)
    )
    outgoing: list[OutgoingInviteOut] = []
    seen: set[str] = set()
    for row in outgoing_rows:
        if row.invitee_email in seen or len(outgoing) >= OUTGOING_LIMIT:
            continue
        seen.add(row.invitee_email)
        expires_at = ensure_utc(row.expires_at)
        expired = row.status == InviteStatus.EXPIRED or (
            expires_at is not None and expires_at <= now
        )
        outgoing.append(
            OutgoingInviteOut(
                id=row.id,
                email=row.invitee_email,
                status="expired" if expired else "pending",
                created_at=row.created_at,
                expires_at=row.expires_at,
            )
        )
    return InvitesOut(incoming=incoming, outgoing=outgoing)


async def list_invites_for_admin(
    db: AsyncSession, *, status: InviteStatus | None = None, limit: int = 100
) -> list[AdminInviteOut]:
    stmt = (
        select(ContactInvite, User.email)
        .join(User, User.id == ContactInvite.inviter_user_id)
        .order_by(ContactInvite.created_at.desc())
        .limit(limit)
    )
    if status is not None:
        stmt = stmt.where(ContactInvite.status == status)
    return [
        AdminInviteOut(
            id=row.id,
            inviter_user_id=row.inviter_user_id,
            inviter_email=inviter_email,
            invitee_email=row.invitee_email,
            invitee_user_id=row.invitee_user_id,
            status=row.status,
            created_at=row.created_at,
            responded_at=row.responded_at,
            expires_at=row.expires_at,
        )
        for row, inviter_email in await db.execute(stmt)
    ]


async def attach_pending_invites(db: AsyncSession, user: User) -> int:
    """Deliver the invitations that were waiting for this address to have an approved account.
    Called on approval, on an open registration and when an administrator changes an email."""
    rows = list(
        await db.scalars(
            select(ContactInvite).where(
                ContactInvite.invitee_email == user.email,
                ContactInvite.status == InviteStatus.PENDING,
                ContactInvite.invitee_user_id.is_(None),
            )
        )
    )
    for row in rows:
        row.invitee_user_id = user.id
    await db.flush()
    return len(rows)


# ---------------------------------------------------------------- trust


async def set_trust(
    db: AsyncSession,
    owner_id: uuid.UUID,
    contact_id: uuid.UUID,
    auto_accept: bool,
    *,
    actor_user_id: uuid.UUID | None = None,
    ip: str | None = None,
) -> Effects:
    """``PUT /contacts/{id}/trust`` (the owner) or an administrator withdrawing a grant.

    Withdrawing never ends a session already running: it decides the *next* request, and the
    owner has the disconnect button for the current one."""
    link = await get_link(db, owner_id, contact_id)
    if link is None:
        raise NotFound("Contact not found")
    if link.auto_accept == auto_accept:
        return Effects()
    link.auto_accept = auto_accept
    link.auto_accept_changed_at = utcnow()
    details: dict[str, Any] = {"contact_user_id": str(contact_id)}
    if actor_user_id is not None and actor_user_id != owner_id:
        details["actor_user_id"] = str(actor_user_id)
    record_event(
        db,
        SecurityEventType.TRUST_GRANTED if auto_accept else SecurityEventType.TRUST_REVOKED,
        user_id=owner_id,
        ip=ip,
        details=details,
    )
    await db.flush()
    return Effects(contacts_changed={owner_id, contact_id})


# ---------------------------------------------------------------- removal


async def _settle_pending(db: AsyncSession, condition: Any, reason: str) -> list[SettledRequest]:
    rows = list(
        await db.scalars(
            select(ConnectionRequest).where(
                ConnectionRequest.status == RequestStatus.PENDING, condition
            )
        )
    )
    now = utcnow()
    settled = []
    for row in rows:
        row.status = RequestStatus.CANCELLED
        row.responded_at = now
        settled.append(
            SettledRequest(
                request_id=row.id,
                initiator=row.initiator,
                guest_user_id=row.guest_user_id,
                guest_device_id=row.guest_device_id,
                host_device_id=row.host_device_id,
                reason=reason,
            )
        )
    await db.flush()
    return settled


def _between(a: uuid.UUID, b: uuid.UUID) -> Any:
    return or_(
        and_(ConnectionRequest.guest_user_id == a, ConnectionRequest.host_user_id == b),
        and_(ConnectionRequest.guest_user_id == b, ConnectionRequest.host_user_id == a),
    )


async def remove_contact(
    db: AsyncSession,
    user_id: uuid.UUID,
    other_id: uuid.UUID,
    *,
    actor_user_id: uuid.UUID | None = None,
    ip: str | None = None,
) -> Effects:
    """Either side ends the relationship; both rows go. Anything pending between the two is
    settled and a live session between them ends as ``contact_removed`` - removing someone is
    also the most decisive way to cut them off."""
    from app.services import sessions as session_service

    result = await db.execute(
        delete(Contact).where(
            or_(
                and_(Contact.user_id == user_id, Contact.contact_user_id == other_id),
                and_(Contact.user_id == other_id, Contact.contact_user_id == user_id),
            )
        )
    )
    if not result.rowcount:
        raise NotFound("Contact not found")
    effects = Effects(contacts_changed={user_id, other_id})
    effects.settled = await _settle_pending(db, _between(user_id, other_id), "contact_removed")
    live = await db.scalar(
        select(Session).where(
            Session.status.in_(NON_ENDED_SESSION_STATUSES),
            or_(
                and_(Session.guest_user_id == user_id, Session.host_user_id == other_id),
                and_(Session.guest_user_id == other_id, Session.host_user_id == user_id),
            ),
        )
    )
    if live is not None:
        effects.session_events.append(
            await session_service.end_session(db, live, SessionEndReason.CONTACT_REMOVED)
        )
    details: dict[str, Any] = {"contact_user_id": str(other_id)}
    if actor_user_id is not None and actor_user_id != user_id:
        details["actor_user_id"] = str(actor_user_id)
    record_event(db, SecurityEventType.CONTACT_REMOVED, user_id=user_id, ip=ip, details=details)
    await db.flush()
    return effects


async def remove_all_for_user(db: AsyncSession, user_id: uuid.UUID) -> Effects:
    """Account deletion: every relationship, invitation and pending question goes."""
    effects = Effects()
    effects.contacts_changed |= set(await contact_ids(db, user_id))
    await db.execute(
        delete(Contact).where(or_(Contact.user_id == user_id, Contact.contact_user_id == user_id))
    )
    invites = list(
        await db.scalars(
            select(ContactInvite).where(
                ContactInvite.status == InviteStatus.PENDING,
                or_(
                    ContactInvite.inviter_user_id == user_id,
                    ContactInvite.invitee_user_id == user_id,
                ),
            )
        )
    )
    now = utcnow()
    for row in invites:
        row.status = InviteStatus.CANCELLED
        row.responded_at = now
        counterpart = row.invitee_user_id if row.inviter_user_id == user_id else row.inviter_user_id
        if counterpart is not None:
            effects.contacts_changed.add(counterpart)
    effects.settled = await _settle_pending(
        db,
        or_(ConnectionRequest.guest_user_id == user_id, ConnectionRequest.host_user_id == user_id),
        "cancelled",
    )
    effects.contacts_changed.discard(user_id)
    await db.flush()
    return effects


async def seed_all_active(db: AsyncSession) -> int:
    """``manage.py seed-contacts --all-active``: make every pair of active accounts contacts.

    The one-off bridge for a deployment upgrading from the global host list: everybody who
    could see everybody before still can, and each person prunes from there."""
    ids = list(
        await db.scalars(select(User.id).where(User.is_active.is_(True), User.deleted_at.is_(None)))
    )
    created = 0
    for i, a in enumerate(ids):
        for b in ids[i + 1 :]:
            if await get_link(db, a, b) is None or await get_link(db, b, a) is None:
                await _connect(db, a, b)
                created += 1
    return created
