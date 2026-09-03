import uuid

from pydantic import BaseModel


class HostOut(BaseModel):
    """Same shape as the ``hosts.snapshot`` WebSocket message entries."""

    device_id: uuid.UUID
    user_display_name: str
    device_name: str
    reachable: bool | None
