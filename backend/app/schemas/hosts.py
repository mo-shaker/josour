import uuid

from pydantic import BaseModel, ConfigDict, Field


class HostOut(BaseModel):
    """Same shape as the ``hosts.snapshot`` WebSocket message entries."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "device_id": "1f0a3d2c-5b7e-4a91-9c33-6d2f8e40b1aa",
                    "user_display_name": "Alice",
                    "device_name": "LAPTOP-01",
                    "reachable": True,
                }
            ]
        }
    )

    device_id: uuid.UUID
    user_display_name: str
    device_name: str
    reachable: bool | None = Field(
        description="Result of the last reachability probe; null when it has not been probed."
    )
