import uuid
from typing import Any

from pydantic import BaseModel, ConfigDict, Field

from app.models.enums import SessionRole


class DiagnosticIn(BaseModel):
    """``POST /diagnostics``; ``data`` is free-form (at most 64 KB serialised)."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "session_id": "3d9c1e77-45b0-4a2e-8f61-9b0d5c7a2e14",
                    "role": "guest",
                    "data": {
                        "upnp_found": True,
                        "mapping_ok": False,
                        "candidates_tried": [{"type": "lan", "ms": 12}],
                    },
                },
                {
                    "session_id": None,
                    "role": "host",
                    "data": {
                        "listener_unauthenticated": 3,
                        "listener_port": 51820,
                        "unauthenticated_peers": ["198.51.100.4", "203.0.113.9"],
                    },
                },
            ]
        }
    )

    session_id: uuid.UUID | None = Field(
        default=None, description="The session this report belongs to; null when there is none."
    )
    role: SessionRole | None = Field(
        default=None, description="Which side of the session reported it."
    )
    data: dict[str, Any] = Field(
        description="Free-form JSON, at most 64 KB serialised. Must never carry browsing "
        "content. The reserved key `listener_unauthenticated` also raises a security event."
    )


class DiagnosticCreated(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={"examples": [{"id": "6b2f0a54-8c31-4d19-a7e0-2f5b9c14d803"}]}
    )

    id: uuid.UUID
