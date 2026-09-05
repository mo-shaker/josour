import uuid

from pydantic import ConfigDict

from app.models.enums import DeviceStatus
from app.schemas.common import ApiModel, UtcDatetime


class MeDeviceOut(ApiModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "1f0a3d2c-5b7e-4a91-9c33-6d2f8e40b1aa",
                    "name": "LAPTOP-01",
                    "os_version": "Windows 11 Pro",
                    "status": "active",
                    "last_seen_at": "2026-09-05T09:14:22.000Z",
                    "created_at": "2026-08-30T07:02:10.000Z",
                }
            ]
        }
    )

    id: uuid.UUID
    name: str
    os_version: str
    status: DeviceStatus
    last_seen_at: UtcDatetime | None
    created_at: UtcDatetime
