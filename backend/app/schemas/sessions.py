import uuid

from pydantic import BaseModel, ConfigDict, Field

from app.models.enums import SessionRole, SessionStatus
from app.schemas.common import UtcDatetime


class MySessionOut(BaseModel):
    """``GET /sessions/me`` entry: the session as seen from the calling user's side."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "3d9c1e77-45b0-4a2e-8f61-9b0d5c7a2e14",
                    "role": "guest",
                    "peer_display_name": "Bob",
                    "peer_device_name": "DESKTOP-02",
                    "status": "ended",
                    "created_at": "2026-09-05T09:00:00.000Z",
                    "started_at": "2026-09-05T09:00:04.000Z",
                    "ended_at": "2026-09-05T09:32:11.000Z",
                    "end_reason": "guest_ended",
                    "bytes_up": 4823104,
                    "bytes_down": 39122944,
                }
            ]
        }
    )

    id: uuid.UUID
    role: SessionRole = Field(description="The calling user's side of this session.")
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

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "3d9c1e77-45b0-4a2e-8f61-9b0d5c7a2e14",
                    "status": "active",
                    "guest_user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "guest_display_name": "Alice",
                    "guest_device_id": "1f0a3d2c-5b7e-4a91-9c33-6d2f8e40b1aa",
                    "guest_device_name": "LAPTOP-01",
                    "host_user_id": "b0f4e7d1-9a26-4c58-8e3f-71c0a5d9b246",
                    "host_display_name": "Bob",
                    "host_device_id": "5e8b2c40-71fa-4d63-9b0e-3c1d7f6a8e92",
                    "host_device_name": "DESKTOP-02",
                    "created_at": "2026-09-05T09:00:00.000Z",
                    "started_at": "2026-09-05T09:00:04.000Z",
                    "expires_at": "2026-09-05T11:00:00.000Z",
                    "ended_at": None,
                    "end_reason": None,
                    "bytes_up": 4823104,
                    "bytes_down": 39122944,
                    "connect_result": "ok",
                    "winner_type": "public",
                    "tls_version": "1.3",
                    "connect_ms": 812,
                }
            ]
        }
    )

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
    connect_result: str | None = Field(
        description="`ok`, `failed` (reported by the clients) or `timeout`."
    )
    winner_type: str | None = Field(
        description="Candidate that won the race: `lan`, `upnp`, `public` or `v6`."
    )
    tls_version: str | None = None
    connect_ms: int | None = None
