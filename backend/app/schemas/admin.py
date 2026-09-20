"""Request/response shapes of the admin API (docs/api.md "Administration")."""

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
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "email": "bob@example.com",
                    "password": "correct-horse-battery",
                    "display_name": "Bob",
                    "role": "user",
                }
            ]
        }
    )

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

    model_config = ConfigDict(
        extra="forbid",
        json_schema_extra={"examples": [{"unlock": True}, {"is_active": False}]},
    )

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

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "email": "alice@example.com",
                    "display_name": "Alice",
                    "role": "user",
                    "is_active": True,
                    "failed_logins": 0,
                    "locked_until": None,
                    "created_at": "2026-08-30T07:02:10.000Z",
                }
            ]
        }
    )

    is_active: bool
    failed_logins: int
    locked_until: UtcDatetime | None
    created_at: UtcDatetime


class AdminDeviceOut(ApiModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "1f0a3d2c-5b7e-4a91-9c33-6d2f8e40b1aa",
                    "user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "name": "LAPTOP-01",
                    "os_version": "Windows 11 Pro",
                    "os_build": "22631",
                    "status": "active",
                    "last_seen_at": "2026-09-05T09:14:22.000Z",
                    "created_at": "2026-08-30T07:02:10.000Z",
                }
            ]
        }
    )

    id: uuid.UUID
    user_id: uuid.UUID
    name: str
    os_version: str
    os_build: str | None
    status: DeviceStatus
    last_seen_at: UtcDatetime | None
    created_at: UtcDatetime


class AdminDomainsIn(BaseModel):
    """Full replacement of the allow-list; a single invalid entry rejects the whole request."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [{"entries": ["example.com", "=exact.com", "portal.corp:8443"]}]
        }
    )

    entries: list[str] = Field(max_length=10_000)

    @field_validator("entries")
    @classmethod
    def _lengths(cls, value: list[str]) -> list[str]:
        if any(len(entry) > 255 for entry in value):
            raise ValueError("entries must be at most 255 characters each")
        return value


class AdminDomainsOut(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "version": 3,
                    "entries": ["example.com", "=exact.com", "portal.corp:8443"],
                    "updated_at": "2026-09-04T18:20:00.000Z",
                }
            ]
        }
    )

    version: int
    entries: list[str]
    updated_at: UtcDatetime | None
    """``null`` only for the implicit empty version 0."""


class SecurityEventOut(ApiModel):
    """One audit row. ``details`` never carries a secret, a URL or any browsing content."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "0a7c9e21-4b38-4f5d-8c60-1e2f3a4b5c6d",
                    "type": "device_revoked",
                    "user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "device_id": "1f0a3d2c-5b7e-4a91-9c33-6d2f8e40b1aa",
                    "ip": None,
                    "details": {
                        "by": "auto",
                        "rule": "device_secret_invalid",
                        "matches": 12,
                        "window_minutes": 60,
                    },
                    "created_at": "2026-09-05T09:40:00.000Z",
                }
            ]
        }
    )

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

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "total_sessions": 128,
                    "sessions_with_connect_result": 121,
                    "connect_ok_ratio": 0.9421487603305785,
                    "winner_type_distribution": {"lan": 14, "public": 92, "upnp": 8, "v6": 7},
                    "tls_version_distribution": {"1.2": 3, "1.3": 118},
                    "connect_diagnostics_count": 340,
                    "end_reason_distribution": {"expired": 11, "guest_ended": 96, "host_ended": 14},
                }
            ]
        }
    )

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
