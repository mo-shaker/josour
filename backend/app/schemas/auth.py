import uuid

from pydantic import BaseModel, Field, field_validator

from app.models.enums import UserRole
from app.schemas.common import ApiModel


class DeviceLogin(BaseModel):
    id: uuid.UUID | None = None
    secret: str | None = Field(default=None, max_length=128)
    name: str = Field(min_length=1, max_length=100)
    os_version: str = Field(min_length=1, max_length=100)
    os_build: str | None = Field(default=None, max_length=50)


class LoginRequest(BaseModel):
    email: str = Field(min_length=3, max_length=320)
    password: str = Field(min_length=1, max_length=1024)
    device: DeviceLogin

    @field_validator("email")
    @classmethod
    def _normalise_email(cls, value: str) -> str:
        value = value.strip().lower()
        local, sep, domain = value.partition("@")
        if not sep or not local or not domain or "." not in domain:
            raise ValueError("must be a valid email address")
        return value


class UserOut(ApiModel):
    id: uuid.UUID
    email: str
    display_name: str
    role: UserRole


class DeviceAuthOut(BaseModel):
    id: uuid.UUID
    name: str
    secret: str | None = None


class TokenResponse(BaseModel):
    access_token: str
    refresh_token: str
    expires_in: int
    user: UserOut
    device: DeviceAuthOut


class RefreshRequest(BaseModel):
    refresh_token: str = Field(min_length=1, max_length=128)


class LogoutRequest(BaseModel):
    refresh_token: str = Field(min_length=1, max_length=128)
