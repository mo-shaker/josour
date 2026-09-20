"""Operator settings (docs/api.md "The settings' default values")."""

from pydantic import BaseModel, ConfigDict, Field, field_validator

from app.models.app_setting import DEFAULT_SETTINGS

MinutesField = Field(ge=1, le=1440)
SecondsField = Field(ge=10, le=300)


def _validate_ports(ports: list[int]) -> list[int]:
    for port in ports:
        if not 1 <= port <= 65535:
            raise ValueError("ports must be integers between 1 and 65535")
    if len(set(ports)) != len(ports):
        raise ValueError("ports must not contain duplicates")
    return ports


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

    @field_validator("allowed_ports")
    @classmethod
    def _ports(cls, value: list[int]) -> list[int]:
        return _validate_ports(value)


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

    @field_validator("allowed_ports")
    @classmethod
    def _ports(cls, value: list[int] | None) -> list[int] | None:
        return None if value is None else _validate_ports(value)
