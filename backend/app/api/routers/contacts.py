"""Contacts, invitations and trust (docs/api.md "Contacts", ADR-0014)."""

import uuid
from typing import Annotated

from fastapi import APIRouter, Depends, Path, Request, Response, status

from app.api.deps import AuthDep, DbDep, client_ip, invite_rate_limit
from app.api.openapi import errors
from app.db.session import commit_retrying_once
from app.schemas.contacts import AcceptedOut, ContactOut, InviteIn, InvitesOut, TrustIn
from app.services import contacts as contact_service
from app.services import effects
from app.services.app_settings import settings_service
from app.ws.connection_manager import connection_manager

router = APIRouter(prefix="/contacts", tags=["contacts"])

ContactId = Annotated[uuid.UUID, Path(description="The contact's user id")]
InviteId = Annotated[uuid.UUID, Path(description="An invitation id")]


@router.get(
    "",
    response_model=list[ContactOut],
    summary="List my contacts",
    responses=errors(401, 403),
)
async def list_contacts(auth: AuthDep, db: DbDep) -> list[ContactOut]:
    """Everyone the caller can share a connection with, in either direction, with both trust
    switches: `i_trust` (they may use my connection without asking me) and `trusts_me` (I may
    use theirs without asking them). `online` is true while any of their devices is connected."""
    return await contact_service.list_contacts(
        db, auth.user.id, is_online=connection_manager.is_user_online
    )


@router.delete(
    "/{user_id}",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Remove a contact",
    responses=errors(401, 403, 404, 422, custom={404: "`not_found` - not one of your contacts."}),
)
async def remove_contact(
    user_id: ContactId, auth: AuthDep, db: DbDep, request: Request
) -> Response:
    """Ends the relationship for both sides. Anything pending between the two is withdrawn, and
    a session running between them ends immediately with `contact_removed`."""
    result = await contact_service.remove_contact(db, auth.user.id, user_id, ip=client_ip(request))
    await db.commit()
    await effects.apply(result)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.put(
    "/{user_id}/trust",
    response_model=ContactOut,
    summary="Let a contact use my connection without asking",
    responses=errors(401, 403, 404, 422, custom={404: "`not_found` - not one of your contacts."}),
)
async def set_trust(
    user_id: ContactId, payload: TrustIn, auth: AuthDep, db: DbDep, request: Request
) -> ContactOut:
    """`auto_accept: true` lets this contact's requests for **my** connection be accepted by my
    app without showing me a prompt; I am still told each time, and can end the session. It is
    about my own connection only - nobody can set it for me. Withdrawing it does not end a
    session already running."""
    result = await contact_service.set_trust(
        db, auth.user.id, user_id, payload.auto_accept, ip=client_ip(request)
    )
    await db.commit()
    await effects.apply(result)
    return await contact_service.get_contact(
        db, auth.user.id, user_id, is_online=connection_manager.is_user_online
    )


@router.get(
    "/invites",
    response_model=InvitesOut,
    summary="List my invitations",
    responses=errors(401, 403),
)
async def list_invites(auth: AuthDep, db: DbDep) -> InvitesOut:
    """`incoming` - people who asked to add me; `outgoing` - addresses I invited. An outgoing
    invitation shows `pending` until it is accepted or expires: a decline is never shown."""
    return await contact_service.list_invites(db, auth.user)


@router.post(
    "/invites",
    status_code=status.HTTP_202_ACCEPTED,
    summary="Invite someone by email",
    dependencies=[Depends(invite_rate_limit)],
    responses=errors(401, 403, 422, 429),
)
async def invite(payload: InviteIn, auth: AuthDep, db: DbDep, request: Request) -> dict[str, str]:
    """Always answers `202 {}` - whether or not the address has an account, and whether or not
    it already invited you (in which case you are now contacts). An address without an account
    receives the invitation if it registers and is approved before it expires."""
    settings = await settings_service.get(db)

    async def invite_once() -> effects.Effects:
        return await contact_service.invite(
            db, auth.user, payload.email, ttl_days=settings.invite_ttl_days, ip=client_ip(request)
        )

    # A double submit: the second is retried and finds the first one's live invitation.
    result = await commit_retrying_once(db, invite_once, reload=(auth.user,))
    await effects.apply(result)
    return {}


@router.post(
    "/invites/{invite_id}/accept",
    response_model=AcceptedOut,
    summary="Accept an invitation",
    responses=errors(401, 403, 404, 422, custom={404: "`not_found` - no such pending invitation."}),
)
async def accept_invite(
    invite_id: InviteId, auth: AuthDep, db: DbDep, request: Request
) -> AcceptedOut:
    async def accept_once() -> tuple[uuid.UUID, effects.Effects]:
        return await contact_service.accept_invite(db, auth.user, invite_id, ip=client_ip(request))

    # Both sides accepting each other's invitation at once: the second is retried and finds the
    # contact rows already written (``_connect`` is idempotent).
    contact_id, result = await commit_retrying_once(db, accept_once, reload=(auth.user,))
    await effects.apply(result)
    contact = await contact_service.get_contact(
        db, auth.user.id, contact_id, is_online=connection_manager.is_user_online
    )
    return AcceptedOut(contact=contact)


@router.post(
    "/invites/{invite_id}/decline",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Decline an invitation",
    responses=errors(401, 403, 404, 422, custom={404: "`not_found` - no such pending invitation."}),
)
async def decline_invite(invite_id: InviteId, auth: AuthDep, db: DbDep) -> Response:
    """Silent: the person who invited you is not told."""
    result = await contact_service.decline_invite(db, auth.user, invite_id)
    await db.commit()
    await effects.apply(result)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.delete(
    "/invites/{invite_id}",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Withdraw an invitation I sent",
    responses=errors(
        401, 403, 404, 422, custom={404: "`not_found` - not one of your invitations."}
    ),
)
async def cancel_invite(invite_id: InviteId, auth: AuthDep, db: DbDep) -> Response:
    result = await contact_service.cancel_invite(db, auth.user, invite_id)
    await db.commit()
    await effects.apply(result)
    return Response(status_code=status.HTTP_204_NO_CONTENT)
