"""Contacts and invitations (docs/api.md "Contacts", ADR-0014)."""

import uuid
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator

from app.schemas.common import UtcDatetime, normalise_email_address


class ContactOut(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "display_name": "Alice",
                    "email": "alice@example.com",
                    "online": True,
                    "i_trust": False,
                    "trusts_me": True,
                    "since": "2026-10-13T09:00:00.000Z",
                }
            ]
        }
    )

    user_id: uuid.UUID
    display_name: str
    email: str
    online: bool = Field(description="At least one of the contact's devices is connected now.")
    i_trust: bool = Field(
        description="I let this contact use my connection without asking me each time."
    )
    trusts_me: bool = Field(
        description="This contact lets me use their connection without asking them each time."
    )
    since: UtcDatetime


class TrustIn(BaseModel):
    model_config = ConfigDict(
        extra="forbid", json_schema_extra={"examples": [{"auto_accept": True}]}
    )

    auto_accept: bool


class InviteIn(BaseModel):
    model_config = ConfigDict(json_schema_extra={"examples": [{"email": "bob@example.com"}]})

    email: str = Field(min_length=3, max_length=320)

    @field_validator("email")
    @classmethod
    def _email(cls, value: str) -> str:
        return normalise_email_address(value)


class IncomingInviteOut(BaseModel):
    id: uuid.UUID
    from_user_id: uuid.UUID
    from_display_name: str
    from_email: str
    created_at: UtcDatetime
    expires_at: UtcDatetime


class OutgoingInviteOut(BaseModel):
    id: uuid.UUID
    email: str
    status: Literal["pending", "expired"]
    """``declined`` is never shown to the inviter: a decline looks exactly like an address that
    has no account, which is what keeps an invitation from telling you who uses Josour."""
    created_at: UtcDatetime
    expires_at: UtcDatetime


class InvitesOut(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "incoming": [
                        {
                            "id": "5b1e0c2a-7d3f-4e18-9a60-2c4d5e6f7a8b",
                            "from_user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                            "from_display_name": "Alice",
                            "from_email": "alice@example.com",
                            "created_at": "2026-10-13T09:00:00.000Z",
                            "expires_at": "2026-11-12T09:00:00.000Z",
                        }
                    ],
                    "outgoing": [
                        {
                            "id": "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d",
                            "email": "carol@example.com",
                            "status": "pending",
                            "created_at": "2026-10-12T18:30:00.000Z",
                            "expires_at": "2026-11-11T18:30:00.000Z",
                        }
                    ],
                }
            ]
        }
    )

    incoming: list[IncomingInviteOut]
    outgoing: list[OutgoingInviteOut]


class AcceptedOut(BaseModel):
    """``POST /contacts/invites/{id}/accept``: the contact that now exists."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "contact": {
                        "user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                        "display_name": "Alice",
                        "email": "alice@example.com",
                        "online": True,
                        "i_trust": False,
                        "trusts_me": False,
                        "since": "2026-10-13T09:05:00.000Z",
                    }
                }
            ]
        }
    )

    contact: ContactOut


class AdminContactOut(BaseModel):
    """One contact of a user, seen by an administrator (``GET /admin/users/{id}/contacts``)."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "contact_user_id": "2f4e6a8c-0b1d-4c3e-9f5a-7b6c8d9e0f1a",
                    "contact_display_name": "Bob",
                    "contact_email": "bob@example.com",
                    "auto_accept": True,
                    "trusted_by_contact": False,
                    "created_at": "2026-10-13T09:05:00.000Z",
                }
            ]
        }
    )

    user_id: uuid.UUID
    contact_user_id: uuid.UUID
    contact_display_name: str
    contact_email: str
    auto_accept: bool
    """The user lets this contact use their connection without asking."""
    trusted_by_contact: bool
    created_at: UtcDatetime


class AdminInviteOut(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d",
                    "inviter_user_id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "inviter_email": "alice@example.com",
                    "invitee_email": "carol@example.com",
                    "invitee_user_id": None,
                    "status": "pending",
                    "created_at": "2026-10-12T18:30:00.000Z",
                    "responded_at": None,
                    "expires_at": "2026-11-11T18:30:00.000Z",
                }
            ]
        }
    )

    id: uuid.UUID
    inviter_user_id: uuid.UUID
    inviter_email: str
    invitee_email: str
    invitee_user_id: uuid.UUID | None
    status: str
    created_at: UtcDatetime
    responded_at: UtcDatetime | None
    expires_at: UtcDatetime
