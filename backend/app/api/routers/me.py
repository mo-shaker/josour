import uuid

from fastapi import APIRouter, Request, Response, status
from sqlalchemy import select

from app.api.deps import AuthDep, DbDep, client_ip
from app.core.errors import NotFound
from app.models import Device
from app.models.enums import DeviceStatus, SecurityEventType
from app.schemas.auth import UserOut
from app.schemas.me import MeDeviceOut
from app.services.security_events import record_event
from app.services.tokens import revoke_device_tokens

router = APIRouter(prefix="/me", tags=["me"])


@router.get("", response_model=UserOut)
async def get_me(auth: AuthDep) -> UserOut:
    return UserOut.model_validate(auth.user)


@router.get("/devices", response_model=list[MeDeviceOut])
async def list_my_devices(auth: AuthDep, db: DbDep) -> list[MeDeviceOut]:
    rows = await db.scalars(
        select(Device).where(Device.user_id == auth.user.id).order_by(Device.created_at)
    )
    return [MeDeviceOut.model_validate(d) for d in rows]


@router.delete("/devices/{device_id}", status_code=status.HTTP_204_NO_CONTENT)
async def revoke_my_device(
    device_id: uuid.UUID, auth: AuthDep, db: DbDep, request: Request
) -> Response:
    device = await db.get(Device, device_id)
    if device is None or device.user_id != auth.user.id:
        raise NotFound("Device not found")
    if device.status != DeviceStatus.REVOKED:
        device.status = DeviceStatus.REVOKED
        await revoke_device_tokens(db, device.id)
        record_event(
            db,
            SecurityEventType.DEVICE_REVOKED,
            user_id=auth.user.id,
            device_id=device.id,
            ip=client_ip(request),
            details={"by": "owner"},
        )
        # TODO(week 3): close the device's WebSocket (4403) and clear its presence row.
        await db.commit()
    return Response(status_code=status.HTTP_204_NO_CONTENT)
