import uuid

from pydantic import BaseModel

from app.models.enums import SessionRole, SessionStatus
from app.schemas.common import UtcDatetime


class MySessionOut(BaseModel):
    """``GET /sessions/me`` entry: the session as seen from the calling user's side."""

    id: uuid.UUID
    role: SessionRole
    peer_display_name: str
    peer_device_name: str
    status: SessionStatus
    created_at: UtcDatetime
    started_at: UtcDatetime | None
    ended_at: UtcDatetime | None
    end_reason: str | None
    bytes_up: int
    bytes_down: int


class AdminSessionOut(BaseModel):
    """``GET /admin/sessions`` entry: both peers named, plus connect diagnostics columns."""

    id: uuid.UUID
    status: SessionStatus
    guest_user_id: uuid.UUID
    guest_display_name: str
    guest_device_id: uuid.UUID
    guest_device_name: str
    host_user_id: uuid.UUID
    host_display_name: str
    host_device_id: uuid.UUID
    host_device_name: str
    created_at: UtcDatetime
    started_at: UtcDatetime | None
    expires_at: UtcDatetime
    ended_at: UtcDatetime | None
    end_reason: str | None
    bytes_up: int
    bytes_down: int
    connect_result: str | None
    winner_type: str | None
    tls_version: str | None
    connect_ms: int | None
