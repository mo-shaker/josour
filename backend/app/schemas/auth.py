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

    status: Literal["pending", "active", "verify"]
    """``pending``: an administrator must approve the account before it can sign in.
    ``active``: sign in now. ``verify``: enter the code mailed to the address
    (``POST /auth/register/confirm``)."""


class RegistrationInfo(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {"mode": "approval", "phone_required": False, "password_reset": False},
                {"mode": "verify", "phone_required": True, "password_reset": True},
            ]
        }
    )

    mode: RegistrationMode
    phone_required: bool = Field(
        description="In `verify` mode, registration also needs a phone number proved by SMS."
    )
    password_reset: bool = Field(
        description="`POST /auth/password/forgot` works: the server sends email."
    )


class _EmailIn(BaseModel):
    email: str = Field(min_length=3, max_length=320)

    @field_validator("email")
    @classmethod
    def _normalise_email(cls, value: str) -> str:
        return normalise_email_address(value)


_CODE = Field(min_length=4, max_length=12, description="The six digits that were sent")


class EmailIn(_EmailIn):
    model_config = ConfigDict(json_schema_extra={"examples": [{"email": "bob@example.com"}]})


class ConfirmEmailIn(_EmailIn):
    model_config = ConfigDict(
        json_schema_extra={"examples": [{"email": "bob@example.com", "code": "042317"}]}
    )

    code: str = _CODE


class SignupStepOut(BaseModel):
    """What ``POST /auth/register/confirm`` (and the phone steps) leave to do."""

    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {"status": "active", "signup_token": None},
                {"status": "phone_required", "signup_token": "<opaque token>"},
            ]
        }
    )

    status: Literal["active", "phone_required"]
    """``active``: the account exists; sign in with ``POST /auth/login``. ``phone_required``:
    send a number to ``POST /auth/register/phone`` with ``signup_token``."""
    signup_token: str | None = Field(
        default=None,
        description="Only with `phone_required`: proves the email step for the phone steps. "
        "Valid until the registration expires (one hour after it began).",
    )


class SignupPhoneIn(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [{"signup_token": "<opaque token>", "phone": "+201001234567"}]
        }
    )

    signup_token: str = Field(min_length=1, max_length=128)
    phone: str = Field(min_length=8, max_length=32, description="International (E.164) format")


class SignupPhoneConfirmIn(BaseModel):
    model_config = ConfigDict(
        json_schema_extra={"examples": [{"signup_token": "<opaque token>", "code": "042317"}]}
    )

    signup_token: str = Field(min_length=1, max_length=128)
    code: str = _CODE


class PasswordResetIn(_EmailIn):
    model_config = ConfigDict(
        json_schema_extra={
            "examples": [
                {"email": "bob@example.com", "code": "042317", "password": "a-new-passphrase"}
            ]
        }
    )

    code: str = _CODE
    password: str = Field(min_length=8, max_length=1024)
