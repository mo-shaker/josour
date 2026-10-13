import uuid
from typing import Annotated

from fastapi import APIRouter, Path, Query, Request, Response, status

from app.api.deps import AdminDep, DbDep, client_ip
from app.api.openapi import errors
from app.models.enums import InviteStatus
from app.schemas.contacts import AdminInviteOut
from app.services import contacts as contact_service
from app.services import effects

router = APIRouter()

OwnerId = Annotated[uuid.UUID, Path(description="The user whose contact it is")]
ContactId = Annotated[uuid.UUID, Path(description="The contact's user id")]


@router.delete(
    "/contacts/{user_id}/{contact_user_id}",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Remove a contact relationship",
    responses=errors(401, 403, 404, 422, custom={404: "`not_found` - they are not contacts."}),
)
async def remove_contact(
    user_id: OwnerId,
    contact_user_id: ContactId,
    auth: AdminDep,
    db: DbDep,
    request: Request,
) -> Response:
    """Removes the relationship for both users, exactly as if one of them had: pending
    questions between them are withdrawn and a session between them ends (`contact_removed`)."""
    result = await contact_service.remove_contact(
        db, user_id, contact_user_id, actor_user_id=auth.user.id, ip=client_ip(request)
    )
    await db.commit()
    await effects.apply(result)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.post(
    "/contacts/{user_id}/{contact_user_id}/revoke-trust",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Withdraw a standing trust grant",
    responses=errors(401, 403, 404, 422, custom={404: "`not_found` - they are not contacts."}),
)
async def revoke_trust(
    user_id: OwnerId,
    contact_user_id: ContactId,
    auth: AdminDep,
    db: DbDep,
    request: Request,
) -> Response:
    """`user_id` stops letting `contact_user_id` use their connection without asking. An
    administrator can only withdraw such a grant, never give one: that decision belongs to the
    person whose address it is."""
    result = await contact_service.set_trust(
        db,
        user_id,
        contact_user_id,
        False,
        actor_user_id=auth.user.id,
        ip=client_ip(request),
    )
    await db.commit()
    await effects.apply(result)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.get(
    "/invites",
    response_model=list[AdminInviteOut],
    summary="List contact invitations",
    responses=errors(401, 403, 422),
)
async def list_invites(
    _: AdminDep,
    db: DbDep,
    status_filter: Annotated[
        InviteStatus | None, Query(alias="status", description="Filter by state")
    ] = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
) -> list[AdminInviteOut]:
    """Newest first, including the address each one was sent to."""
    return await contact_service.list_invites_for_admin(db, status=status_filter, limit=limit)
