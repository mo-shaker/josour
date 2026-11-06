"""Workspaces (ADR-0018): ``/workspace``, ``/me/workspace-invites`` and ``/admin/workspaces``."""

import uuid
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator

from app.models.enums import WorkspaceRole
from app.schemas.common import ApiModel, UtcDatetime, normalise_email_address


class WorkspaceOut(ApiModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "5a3b1c2d-0e4f-4a6b-8c7d-9e0f1a2b3c4d",
                    "name": "Acme Trading",
                    "role": "owner",
                    "member_count": 12,
                }
            ]
        }
    )

    id: uuid.UUID
    name: str
    role: WorkspaceRole = Field(description="The caller's role in it")
    member_count: int


class WorkspacePatch(BaseModel):
    model_config = ConfigDict(extra="forbid", json_schema_extra={"examples": [{"name": "Acme"}]})

    name: str = Field(min_length=1, max_length=100)

    @field_validator("name")
    @classmethod
    def _name(cls, value: str) -> str:
        value = value.strip()
        if not value:
            raise ValueError("must not be empty")
        return value


class MemberOut(ApiModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "display_name": "Alice",
                    "email": "alice@example.com",
                    "role": "admin",
                    "state": "active",
                    "online": True,
                    "joined_at": "2026-11-06T09:00:00.000Z",
                }
            ]
        }
    )

    user_id: uuid.UUID
    display_name: str
    email: str
    role: WorkspaceRole
    state: str = Field(description="`active`, `disabled`, `pending` or `rejected`")
    online: bool
    joined_at: UtcDatetime = Field(description="When the account was created")


class MemberPatch(BaseModel):
    model_config = ConfigDict(extra="forbid", json_schema_extra={"examples": [{"role": "admin"}]})

    role: WorkspaceRole = Field(
        description="`owner` hands the workspace over: the caller (who must be the owner) becomes "
        "an `admin`."
    )


class WorkspaceInviteIn(BaseModel):
    model_config = ConfigDict(
        extra="forbid",
        json_schema_extra={"examples": [{"email": "colleague@example.com", "role": "member"}]},
    )

    email: str = Field(min_length=3, max_length=320)
    role: Literal["member", "admin"] = "member"

    @field_validator("email")
    @classmethod
    def _normalise_email(cls, value: str) -> str:
        return normalise_email_address(value)


class WorkspaceInviteOut(ApiModel):
    """An invitation the workspace sent (``GET /workspace/invites``)."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "0b9e2a61-7c4d-4f3e-9a8b-1c2d3e4f5a6b",
                    "email": "colleague@example.com",
                    "role": "member",
                    "status": "pending",
                    "created_at": "2026-11-06T09:00:00.000Z",
                    "expires_at": "2026-12-06T09:00:00.000Z",
                }
            ]
        }
    )

    id: uuid.UUID
    email: str
    role: WorkspaceRole
    status: Literal["pending", "expired"]
    created_at: UtcDatetime
    expires_at: UtcDatetime


class IncomingWorkspaceInviteOut(ApiModel):
    """An invitation addressed to the caller (``GET /me/workspace-invites``)."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "0b9e2a61-7c4d-4f3e-9a8b-1c2d3e4f5a6b",
                    "workspace_id": "5a3b1c2d-0e4f-4a6b-8c7d-9e0f1a2b3c4d",
                    "workspace_name": "Acme Trading",
                    "role": "member",
                    "from_display_name": "Alice",
                    "created_at": "2026-11-06T09:00:00.000Z",
                    "expires_at": "2026-12-06T09:00:00.000Z",
                }
            ]
        }
    )

    id: uuid.UUID
    workspace_id: uuid.UUID
    workspace_name: str
    role: WorkspaceRole
    from_display_name: str | None
    created_at: UtcDatetime
    expires_at: UtcDatetime


class MeOut(ApiModel):
    """``GET /me``: the account plus where it belongs."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "email": "alice@example.com",
                    "display_name": "Alice",
                    "role": "user",
                    "workspace": {
                        "id": "5a3b1c2d-0e4f-4a6b-8c7d-9e0f1a2b3c4d",
                        "name": "Acme Trading",
                        "role": "member",
                        "member_count": 12,
                    },
                    "workspace_mode": "multi",
                    "phone": "+201001234567",
                }
            ]
        }
    )

    id: uuid.UUID
    email: str
    display_name: str
    role: str
    workspace: WorkspaceOut
    workspace_mode: Literal["single", "multi"]
    phone: str | None


class AdminWorkspaceOut(ApiModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "5a3b1c2d-0e4f-4a6b-8c7d-9e0f1a2b3c4d",
                    "name": "Acme Trading",
                    "is_default": False,
                    "member_count": 12,
                    "owner_user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "owner_email": "alice@example.com",
                    "created_at": "2026-11-06T09:00:00.000Z",
                }
            ]
        }
    )

    id: uuid.UUID
    name: str
    is_default: bool
    member_count: int
    owner_user_id: uuid.UUID | None
    owner_email: str | None
    created_at: UtcDatetime
