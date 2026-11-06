"""The caller's workspace (ADR-0018). Every route answers ``403 workspaces_disabled`` while the
server is in ``single`` mode."""

import uuid
from typing import Annotated

from fastapi import APIRouter, Depends, Path, Request, Response, status

from app.api.deps import AuthDep, DbDep, SendersDep, client_ip, invite_rate_limit
from app.api.openapi import errors
from app.models.enums import WorkspaceRole
from app.schemas.workspaces import (
    MemberOut,
    MemberPatch,
    WorkspaceInviteIn,
    WorkspaceInviteOut,
    WorkspaceOut,
    WorkspacePatch,
)
from app.services import effects, outbox
from app.services import workspaces as workspace_service
from app.services.app_settings import settings_service
from app.ws.connection_manager import connection_manager

MemberId = Annotated[uuid.UUID, Path(description="A member's user id")]
InviteId = Annotated[uuid.UUID, Path(description="An invitation id")]

_DISABLED = "`workspaces_disabled` - the server keeps everybody in one workspace (`single` mode)."
_MANAGERS = "`forbidden` - only the owner or an admin can do this."


async def _enabled(_: AuthDep, db: DbDep) -> None:
    """Authenticated first, so a caller without a token gets 401 like everywhere else."""
    await workspace_service.ensure_enabled(db)


router = APIRouter(prefix="/workspace", tags=["workspace"], dependencies=[Depends(_enabled)])


@router.get(
    "",
    response_model=WorkspaceOut,
    summary="Get my workspace",
    responses=errors(401, 403, custom={403: _DISABLED}),
)
async def get_workspace(auth: AuthDep, db: DbDep) -> WorkspaceOut:
    """The workspace the caller belongs to and their role in it."""
    return await workspace_service.describe(db, auth.user)


@router.patch(
    "",
    response_model=WorkspaceOut,
    summary="Rename my workspace",
    responses=errors(401, 403, 422, custom={403: f"{_DISABLED} {_MANAGERS}"}),
)
async def rename_workspace(
    payload: WorkspacePatch, auth: AuthDep, db: DbDep, request: Request
) -> WorkspaceOut:
    """The name members see, and that invitations name. The owner or an admin."""
    result = await workspace_service.rename(db, auth.user, payload.name, ip=client_ip(request))
    await db.commit()
    return result


@router.get(
    "/members",
    response_model=list[MemberOut],
    summary="List the members of my workspace",
    responses=errors(401, 403, custom={403: _DISABLED}),
)
async def list_members(auth: AuthDep, db: DbDep) -> list[MemberOut]:
    """Everyone in the caller's workspace. Being a member does not make two people contacts:
    sharing a connection still needs a contact invitation (`POST /contacts/invites`), which in
    `multi` mode can only reach somebody in this list."""
    return await workspace_service.list_members(
        db, auth.user, is_online=connection_manager.is_user_online
    )


@router.patch(
    "/members/{user_id}",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Change a member's role",
    responses=errors(
        401,
        403,
        404,
        409,
        422,
        custom={403: f"{_DISABLED} `forbidden` - only the owner changes roles."},
    ),
)
async def change_role(
    user_id: MemberId, payload: MemberPatch, auth: AuthDep, db: DbDep, request: Request
) -> Response:
    """`admin` or `member`; `owner` hands the workspace over and makes the caller an admin."""
    await workspace_service.set_member_role(
        db, auth.user, user_id, WorkspaceRole(payload.role), ip=client_ip(request)
    )
    await db.commit()
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.delete(
    "/members/{user_id}",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Remove a member",
    responses=errors(401, 403, 404, 409, 422, custom={403: f"{_DISABLED} {_MANAGERS}"}),
)
async def remove_member(user_id: MemberId, auth: AuthDep, db: DbDep, request: Request) -> Response:
    """The person keeps their account, in a new workspace of their own. Their contacts in this
    workspace are removed, and a session they are in ends as `contact_removed`. The owner can
    remove anyone but themselves; an admin can remove members only."""
    result = await workspace_service.remove_member(db, auth.user, user_id, ip=client_ip(request))
    await db.commit()
    await effects.apply(result)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.post(
    "/leave",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Leave my workspace",
    responses=errors(
        401,
        403,
        409,
        custom={
            403: _DISABLED,
            409: "`owner_must_transfer` - the owner hands the workspace over first.",
        },
    ),
)
async def leave(auth: AuthDep, db: DbDep, request: Request) -> Response:
    """Moves the caller into a new workspace of their own; contacts here are removed."""
    result = await workspace_service.leave(db, auth.user, ip=client_ip(request))
    await db.commit()
    await effects.apply(result)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.get(
    "/invites",
    response_model=list[WorkspaceInviteOut],
    summary="List the invitations my workspace sent",
    responses=errors(401, 403, custom={403: f"{_DISABLED} {_MANAGERS}"}),
)
async def list_invites(auth: AuthDep, db: DbDep) -> list[WorkspaceInviteOut]:
    """Invitations still waiting for an answer, and expired ones until they are withdrawn.
    Accepted and declined ones are not listed: the address simply appears among the members,
    or does not (a decline is silent, like a contact invitation's)."""
    return await workspace_service.list_sent(db, auth.user)


@router.post(
    "/invites",
    status_code=status.HTTP_202_ACCEPTED,
    response_class=Response,
    summary="Invite somebody into my workspace",
    dependencies=[Depends(invite_rate_limit)],
    responses=errors(
        401,
        403,
        422,
        429,
        custom={403: f"{_DISABLED} {_MANAGERS} Only the owner invites an admin."},
    ),
)
async def invite(
    payload: WorkspaceInviteIn,
    auth: AuthDep,
    db: DbDep,
    request: Request,
    senders: SendersDep,
) -> Response:
    """Answers `202` with no body whatever the address is - a member already, an account
    elsewhere, or nobody yet - so it cannot be used to find out who uses the service. The
    address is emailed (when the server sends mail) and the person finds the invitation in the
    app under `GET /me/workspace-invites` once they sign in with it. Shares the per-user budget
    of contact invitations."""
    ttl = (await settings_service.get(db)).invite_ttl_days
    out = await workspace_service.invite(
        db,
        auth.user,
        payload.email,
        WorkspaceRole(payload.role),
        ttl_days=ttl,
        ip=client_ip(request),
    )
    await db.commit()
    await outbox.deliver(senders, out)
    return Response(status_code=status.HTTP_202_ACCEPTED)


@router.delete(
    "/invites/{invite_id}",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Withdraw an invitation",
    responses=errors(401, 403, 404, 422, custom={403: f"{_DISABLED} {_MANAGERS}"}),
)
async def cancel_invite(invite_id: InviteId, auth: AuthDep, db: DbDep) -> Response:
    """The invitation disappears from the invitee's list. The owner or an admin."""
    await workspace_service.cancel(db, auth.user, invite_id)
    await db.commit()
    return Response(status_code=status.HTTP_204_NO_CONTENT)
