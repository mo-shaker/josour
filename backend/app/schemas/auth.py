import uuid
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator

from app.models.enums import RegistrationMode, UserRole
from app.schemas.common import ApiModel, normalise_email_address

_DEVICE_EXAMPLE = {
    "id": "1f0a3d2c-5b7e-4a91-9c33-6d2f8e40b1aa",
    "secret": "<opaque device secret, stored with DPAPI>",
    "name": "LAPTOP-01",
    "os_version": "Windows 11 Pro",
    "os_build": "22631",
}


class DeviceLogin(BaseModel):
    """The calling machine. ``id`` and ``secret`` are null on a device's first sign-in."""

    id: uuid.UUID | None = Field(
        default=None, description="Null on first sign-in; the server registers a new device."
    )
    secret: str | None = Field(
        default=None,
        max_length=128,
        description="The secret returned once at registration; null on first sign-in.",
    )
    name: str = Field(min_length=1, max_length=100, description="Machine name, shown to the peer")
    os_version: str = Field(min_length=1, max_length=100)
    os_build: str | None = Field(default=None, max_length=50)


class LoginRequest(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "email": "alice@example.com",
                    "password": "correct-horse-battery",
                    "device": {**_DEVICE_EXAMPLE, "id": None, "secret": None},
                },
                {
                    "email": "alice@example.com",
                    "password": "correct-horse-battery",
                    "device": _DEVICE_EXAMPLE,
                },
            ]
        }
    )

    email: str = Field(min_length=3, max_length=320)
    password: str = Field(min_length=1, max_length=1024)
    device: DeviceLogin

    @field_validator("email")
    @classmethod
    def _normalise_email(cls, value: str) -> str:
        return normalise_email_address(value)


class UserOut(ApiModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                    "email": "alice@example.com",
                    "display_name": "Alice",
                    "role": "user",
                }
            ]
        }
    )

    id: uuid.UUID
    email: str
    display_name: str
    role: UserRole


class DeviceAuthOut(BaseModel):
    id: uuid.UUID
    name: str
    secret: str | None = Field(
        default=None,
        description="Returned **once**, at device registration. Null on every later response; "
        "store it with DPAPI, because it cannot be retrieved again.",
    )


class TokenResponse(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "access_token": "<access JWT>",
                    "refresh_token": "<opaque refresh token>",
                    "expires_in": 900,
                    "user": {
                        "id": "8c1d4b6a-2e70-4f18-90a5-3b7c6e2d10ff",
                        "email": "alice@example.com",
                        "display_name": "Alice",
                        "role": "user",
                    },
                    "device": {
                        "id": "1f0a3d2c-5b7e-4a91-9c33-6d2f8e40b1aa",
                        "name": "LAPTOP-01",
                        "secret": None,
                    },
                }
            ]
        }
    )

    access_token: str
    refresh_token: str
    expires_in: int = Field(description="Lifetime of the access token in seconds")
    user: UserOut
    device: DeviceAuthOut


class RefreshRequest(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={"examples": [{"refresh_token": "<opaque refresh token>"}]}
    )

    refresh_token: str = Field(min_length=1, max_length=128)


class LogoutRequest(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={"examples": [{"refresh_token": "<opaque refresh token>"}]}
    )

    refresh_token: str = Field(min_length=1, max_length=128)


class RegisterRequest(BaseModel):
    """``POST /auth/register``. No device: the app signs in with ``POST /auth/login`` once the
    account may be used, and registers its device then, exactly as for any other account."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {
                    "email": "bob@example.com",
                    "password": "correct-horse-battery",
                    "display_name": "Bob",
                }
            ]
        }
    )

    email: str = Field(min_length=3, max_length=320)
    password: str = Field(min_length=8, max_length=1024)
    display_name: str = Field(min_length=1, max_length=100)

    @field_validator("email")
    @classmethod
    def _normalise_email(cls, value: str) -> str:
        return normalise_email_address(value)

    @field_validator("display_name")
    @classmethod
    def _name(cls, value: str) -> str:
        value = value.strip()
        if not value:
            raise ValueError("must not be empty")
        return value


class RegisterOut(BaseModel):
    model_config = ConfigDict(json_schema_extra={"examples": [{"status": "pending"}]})

    status: Literal["pending", "active"]
    """``pending``: an administrator must approve the account before it can sign in.
    ``active``: sign in now."""


class RegistrationInfo(BaseModel):
    model_config = ConfigDict(json_schema_extra={"examples": [{"mode": "approval"}]})

    mode: RegistrationMode
