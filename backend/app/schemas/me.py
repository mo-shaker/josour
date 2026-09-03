import uuid

from app.models.enums import DeviceStatus
from app.schemas.common import ApiModel, UtcDatetime


class MeDeviceOut(ApiModel):
    id: uuid.UUID
    name: str
    os_version: str
    status: DeviceStatus
    last_seen_at: UtcDatetime | None
    created_at: UtcDatetime
