import uuid
from typing import Any

from pydantic import BaseModel

from app.models.enums import SessionRole


class DiagnosticIn(BaseModel):
    """``POST /diagnostics``; ``data`` is free-form (at most 64 KB serialised)."""

    session_id: uuid.UUID | None = None
    role: SessionRole | None = None
    data: dict[str, Any]


class DiagnosticCreated(BaseModel):
    id: uuid.UUID
