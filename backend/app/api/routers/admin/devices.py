import uuid
from typing import Annotated

from fastapi import APIRouter, Path, Query, Request, Response, status

from app.api.deps import AdminDep, DbDep, client_ip
from app.api.openapi import errors
from app.core.errors import NotFound
from app.models import Device
from app.schemas.admin import AdminDeviceOut
from app.services import devices as device_service
from app.ws import notify
from app.ws.protocol import CloseCode

router = APIRouter(prefix="/devices")


@router.get(
    "",
    response_model=list[AdminDeviceOut],
    summary="List devices",
    responses=errors(401, 403, 422),
)
async def list_devices(
    _: AdminDep,
    db: DbDep,
    user_id: Annotated[uuid.UUID | None, Query(description="Only this user's devices")] = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
) -> list[AdminDeviceOut]:
    """Registered devices, newest first, with their owner, OS build and last-seen time."""
    rows = await device_service.list_devices(db, user_id=user_id, limit=limit)
    return [AdminDeviceOut.model_validate(d) for d in rows]


@router.post(
    "/{device_id}/revoke",
    status_code=status.HTTP_204_NO_CONTENT,
    response_class=Response,
    summary="Revoke a device",
    responses=errors(401, 403, 404, 422, custom={404: "`not_found` - no device with that id."}),
)
async def revoke_device(
    device_id: Annotated[uuid.UUID, Path(description="Device to revoke")],
    auth: AdminDep,
    db: DbDep,
    request: Request,
) -> Response:
    """Blocks a device: its refresh tokens are revoked, its presence is cleared, its live control
    channel is closed with WebSocket code 4403, and a `device_revoked` row is written to the
    audit trail naming the administrator who did it.

    Idempotent - revoking an already revoked device also answers `204` and writes nothing.

    The same revocation happens automatically when a device matches a detection rule (ADR-0008);
    those rows carry `by: "auto"` and the rule that fired.
    """
    device = await db.get(Device, device_id)
    if device is None:
        raise NotFound("Device not found")
    changed = await device_service.revoke_device(
        db, device, by="admin", actor_user_id=auth.user.id, ip=client_ip(request)
    )
    if changed:
        await db.commit()
        # The token is dead, so the live control channel must go too (section 7).
        await notify.close_device(device.id, CloseCode.NOT_ALLOWED)
    return Response(status_code=status.HTTP_204_NO_CONTENT)
