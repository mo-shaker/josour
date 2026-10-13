"""Operator settings (docs/api.md "The settings' default values")."""

import re

from pydantic import BaseModel, ConfigDict, Field, field_validator

from app.models.app_setting import DEFAULT_SETTINGS
from app.models.enums import RegistrationMode

MinutesField = Field(ge=1, le=1440)
SecondsField = Field(ge=10, le=300)


def _validate_ports(ports: list[int]) -> list[int]:
    for port in ports:
        if not 1 <= port <= 65535:
            raise ValueError("ports must be integers between 1 and 65535")
    if len(set(ports)) != len(ports):
        raise ValueError("ports must not contain duplicates")
    return ports


_VERSION = re.compile(r"\d{1,6}(\.\d{1,6}){0,3}")


def _validate_version(value: str) -> str:
    """Empty (no floor) or a dotted numeric version such as ``2.0`` or ``2.0.1``."""
    value = value.strip()
    if value and _VERSION.fullmatch(value) is None:
        raise ValueError("must be empty or a dotted numeric version such as 2.0.1")
    return value


class AppSettings(BaseModel):
    """Effective settings: the docs/api.md defaults overridden by ``app_settings`` rows."""

    model_config = ConfigDict(
        frozen=True,
        json_schema_extra={
            "examples": [
                {
                    "max_session_minutes": 120,
                    "request_timeout_seconds": 60,
                    "connect_timeout_seconds": 30,
                    "log_domains": False,
                    "enforce_allowlist": False,
                    "allowed_ports": [80, 443],
                    "registration_mode": "approval",
                    "min_client_version": "",
                    "invite_ttl_days": 30,
                }
            ]
        },
    )

    max_session_minutes: int = Field(DEFAULT_SETTINGS["max_session_minutes"], ge=1, le=1440)
    request_timeout_seconds: int = Field(DEFAULT_SETTINGS["request_timeout_seconds"], ge=10, le=300)
    connect_timeout_seconds: int = Field(DEFAULT_SETTINGS["connect_timeout_seconds"], ge=10, le=300)
    log_domains: bool = DEFAULT_SETTINGS["log_domains"]
    enforce_allowlist: bool = DEFAULT_SETTINGS["enforce_allowlist"]
    allowed_ports: list[int] = Field(
        default_factory=lambda: list(DEFAULT_SETTINGS["allowed_ports"]), min_length=1
    )
    registration_mode: RegistrationMode = RegistrationMode(DEFAULT_SETTINGS["registration_mode"])
    min_client_version: str = Field(DEFAULT_SETTINGS["min_client_version"], max_length=32)
    invite_ttl_days: int = Field(DEFAULT_SETTINGS["invite_ttl_days"], ge=1, le=365)

    @field_validator("allowed_ports")
    @classmethod
    def _ports(cls, value: list[int]) -> list[int]:
        return _validate_ports(value)

    @field_validator("min_client_version")
    @classmethod
    def _version(cls, value: str) -> str:
        return _validate_version(value)


class SettingsPatch(BaseModel):
    """``PATCH /admin/settings`` body; every field optional, unknown keys rejected."""

    model_config = ConfigDict(
        extra="forbid",
        json_schema_extra={"examples": [{"max_session_minutes": 60, "log_domains": False}]},
    )

    max_session_minutes: int | None = Field(None, ge=1, le=1440)
    request_timeout_seconds: int | None = Field(None, ge=10, le=300)
    connect_timeout_seconds: int | None = Field(None, ge=10, le=300)
    log_domains: bool | None = None
    enforce_allowlist: bool | None = None
    allowed_ports: list[int] | None = Field(None, min_length=1)
    registration_mode: RegistrationMode | None = None
    min_client_version: str | None = Field(None, max_length=32)
    invite_ttl_days: int | None = Field(None, ge=1, le=365)

    @field_validator("allowed_ports")
    @classmethod
    def _ports(cls, value: list[int] | None) -> list[int] | None:
        return None if value is None else _validate_ports(value)

    @field_validator("min_client_version")
    @classmethod
    def _version(cls, value: str | None) -> str | None:
        return None if value is None else _validate_version(value)
