import uuid
from typing import Annotated

from fastapi import APIRouter, Path, Request, Response, status
from sqlalchemy import select

from app.api.deps import AuthDep, DbDep, client_ip
from app.api.openapi import errors
from app.core.errors import NotFound
from app.models import Device
from app.models.enums import WorkspaceMode
from app.schemas.me import MeDeviceOut
from app.schemas.workspaces import IncomingWorkspaceInviteOut, MeOut
from app.services import effects
from app.services import workspaces as workspace_service
from app.services.devices import revoke_device
from app.ws import notify
from app.ws.protocol import CloseCode

router = APIRouter(prefix="/me", tags=["me"])


InviteId = Annotated[uuid.UUID, Path(description="A workspace invitation id")]


@router.get(
    "",
    response_model=MeOut,
    summary="Get the signed-in user",
    responses=errors(401, 403),
)
async def get_me(auth: AuthDep, db: DbDep) -> MeOut:
    """The account behind the presented access token, with its workspace (ADR-0018) and the
    server's `workspace_mode`: the app shows workspace management only in `multi` mode."""
    user = auth.user
    return MeOut(
        id=user.id,
        email=user.email,
        display_name=user.display_name,
        role=user.role,
        workspace=await workspace_service.describe(db, user),
        workspace_mode=(await workspace_service.current_mode(db)).value,
        phone=user.phone,
    )


@router.get(
    "/workspace-invites",
    response_model=list[IncomingWorkspaceInviteOut],
    summary="List invitations to join a workspace",
    responses=errors(401, 403),
)
async def list_workspace_invites(auth: AuthDep, db: DbDep) -> list[IncomingWorkspaceInviteOut]:
    """Live invitations addressed to the caller's email by other workspaces. Always empty in
    `single` mode."""
    if await workspace_service.current_mode(db) != WorkspaceMode.MULTI:
        return []
    return await workspace_service.incoming(db, auth.user)


@router.post(
    "/workspace-invites/{invite_id}/accept",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Join a workspace",
    responses=errors(
        401,
        403,
        404,
        409,
        422,
        custom={
            403: "`workspaces_disabled` in `single` mode.",
            409: "`owner_must_transfer` - the caller owns a workspace that has other members.",
        },
    ),
)
async def accept_workspace_invite(
    invite_id: InviteId, auth: AuthDep, db: DbDep, request: Request
) -> Response:
    """Moves the caller into the inviting workspace with the role the invitation names. Every
    contact the caller had is removed - contacts never cross a workspace - and a session they
    are in ends as `contact_removed`. A workspace left with nobody in it is deleted."""
    await workspace_service.ensure_enabled(db)
    result = await workspace_service.accept(db, auth.user, invite_id, ip=client_ip(request))
    await db.commit()
    await effects.apply(result)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.post(
    "/workspace-invites/{invite_id}/decline",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Decline a workspace invitation",
    responses=errors(401, 403, 404, 422, custom={403: "`workspaces_disabled` in `single` mode."}),
)
async def decline_workspace_invite(invite_id: InviteId, auth: AuthDep, db: DbDep) -> Response:
    """Silent: the workspace keeps seeing the invitation as pending until it expires."""
    await workspace_service.ensure_enabled(db)
    await workspace_service.decline(db, auth.user, invite_id)
    await db.commit()
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.get(
    "/devices",
    response_model=list[MeDeviceOut],
    summary="List my registered devices",
    responses=errors(401, 403),
)
async def list_my_devices(auth: AuthDep, db: DbDep) -> list[MeDeviceOut]:
    """Every device registered to the calling user, oldest first, revoked ones included."""
    rows = await db.scalars(
        select(Device).where(Device.user_id == auth.user.id).order_by(Device.created_at)
    )
    return [MeDeviceOut.model_validate(d) for d in rows]


@router.delete(
    "/devices/{device_id}",
    status_code=status.HTTP_204_NO_CONTENT,
    summary="Revoke one of my devices",
    responses=errors(
        401,
        403,
        404,
        422,
        custom={404: "`not_found` - no such device, or it belongs to another user."},
    ),
)
async def revoke_my_device(
    device_id: Annotated[uuid.UUID, Path(description="One of the caller's own devices")],
    auth: AuthDep,
    db: DbDep,
    request: Request,
) -> Response:
    """Un-registers a device the caller owns: its refresh tokens are revoked, its presence is
    cleared, and its live control channel is closed immediately with WebSocket code 4403.

    Idempotent - revoking an already revoked device also answers `204`.
    """
    device = await db.get(Device, device_id)
    if device is None or device.user_id != auth.user.id:
        raise NotFound("Device not found")
    changed = await revoke_device(
        db, device, by="owner", actor_user_id=auth.user.id, ip=client_ip(request)
    )
    if changed:
        await db.commit()
        await notify.close_device(device.id, CloseCode.NOT_ALLOWED)
    return Response(status_code=status.HTTP_204_NO_CONTENT)
