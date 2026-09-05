import uuid
from typing import Annotated

from fastapi import APIRouter, Path, Request, Response, status
from sqlalchemy import select

from app.api.deps import AuthDep, DbDep, client_ip
from app.api.openapi import errors
from app.core.errors import NotFound
from app.models import Device
from app.schemas.auth import UserOut
from app.schemas.me import MeDeviceOut
from app.services.devices import revoke_device
from app.ws import notify
from app.ws.protocol import CloseCode

router = APIRouter(prefix="/me", tags=["me"])


@router.get(
    "",
    response_model=UserOut,
    summary="Get the signed-in user",
    responses=errors(401, 403),
)
async def get_me(auth: AuthDep) -> UserOut:
    """The account behind the presented access token."""
    return UserOut.model_validate(auth.user)


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
