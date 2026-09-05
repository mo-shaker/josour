"""Request/response shapes of the admin API (docs/api.md "الإدارة")."""

import uuid
from typing import Any

from pydantic import BaseModel, ConfigDict, Field, field_validator

from app.models.enums import DeviceStatus, UserRole
from app.schemas.auth import UserOut
from app.schemas.common import ApiModel, UtcDatetime, normalise_email_address


def _strip_non_empty(value: str) -> str:
    value = value.strip()
    if not value:
        raise ValueError("must not be empty")
    return value


class AdminUserCreate(BaseModel):
    email: str = Field(min_length=3, max_length=320)
    password: str = Field(min_length=8, max_length=1024)
    display_name: str = Field(min_length=1, max_length=100)
    role: UserRole = UserRole.USER

    @field_validator("email")
    @classmethod
    def _email(cls, value: str) -> str:
        return normalise_email_address(value)

    @field_validator("display_name")
    @classmethod
    def _name(cls, value: str) -> str:
        return _strip_non_empty(value)


class AdminUserPatch(BaseModel):
    """``{ is_active?, password?, display_name?, unlock?: true }``; unknown keys rejected."""

    model_config = ConfigDict(extra="forbid")

    is_active: bool | None = None
    password: str | None = Field(None, min_length=8, max_length=1024)
    display_name: str | None = Field(None, min_length=1, max_length=100)
    unlock: bool | None = None

    @field_validator("display_name")
    @classmethod
    def _name(cls, value: str | None) -> str | None:
        return None if value is None else _strip_non_empty(value)


class AdminUserOut(UserOut):
    """``UserOut`` plus the account-state fields an administrator acts on."""

    is_active: bool
    failed_logins: int
    locked_until: UtcDatetime | None
    created_at: UtcDatetime


class AdminDeviceOut(ApiModel):
    id: uuid.UUID
    user_id: uuid.UUID
    name: str
    os_version: str
    os_build: str | None
    status: DeviceStatus
    last_seen_at: UtcDatetime | None
    created_at: UtcDatetime


class AdminDomainsIn(BaseModel):
    entries: list[str] = Field(max_length=10_000)

    @field_validator("entries")
    @classmethod
    def _lengths(cls, value: list[str]) -> list[str]:
        if any(len(entry) > 255 for entry in value):
            raise ValueError("entries must be at most 255 characters each")
        return value


class AdminDomainsOut(BaseModel):
    version: int
    entries: list[str]
    updated_at: UtcDatetime | None
    """``null`` only for the implicit empty version 0."""


class SecurityEventOut(ApiModel):
    id: uuid.UUID
    type: str
    user_id: uuid.UUID | None
    device_id: uuid.UUID | None
    ip: str | None
    details: dict[str, Any] | None
    created_at: UtcDatetime


class DiagnosticsSummary(BaseModel):
    """Relay decision-gate summary (``GET /admin/diagnostics``). All zeros/empty without data.

    Additive only: week 4 filled the connect columns with real data and added
    ``end_reason_distribution``; no existing field was renamed or changed shape."""

    total_sessions: int
    sessions_with_connect_result: int
    connect_ok_ratio: float
    """``connect_result == 'ok'`` over sessions with a non-null ``connect_result``. The other
    values are ``failed`` (the clients reported it) and ``timeout`` (the connect deadline)."""
    winner_type_distribution: dict[str, int]
    tls_version_distribution: dict[str, int]
    connect_diagnostics_count: int
    end_reason_distribution: dict[str, int]
    """Ended sessions per ``end_reason`` (docs/ws-protocol.md section 5)."""
