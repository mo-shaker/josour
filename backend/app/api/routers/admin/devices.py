import uuid
from typing import Annotated

from fastapi import APIRouter, Query, Request, Response, status

from app.api.deps import AdminDep, DbDep, client_ip
from app.core.errors import NotFound
from app.models import Device
from app.schemas.admin import AdminDeviceOut
from app.services import devices as device_service
from app.ws import notify
from app.ws.protocol import CloseCode

router = APIRouter(prefix="/devices")


@router.get("", response_model=list[AdminDeviceOut])
async def list_devices(
    _: AdminDep,
    db: DbDep,
    user_id: uuid.UUID | None = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
) -> list[AdminDeviceOut]:
    rows = await device_service.list_devices(db, user_id=user_id, limit=limit)
    return [AdminDeviceOut.model_validate(d) for d in rows]


@router.post("/{device_id}/revoke", status_code=status.HTTP_204_NO_CONTENT, response_class=Response)
async def revoke_device(
    device_id: uuid.UUID, auth: AdminDep, db: DbDep, request: Request
) -> Response:
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
