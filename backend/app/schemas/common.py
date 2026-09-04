"""Shared schema building blocks."""

from datetime import datetime
from typing import Annotated

from pydantic import BaseModel, ConfigDict, PlainSerializer

from app.core.clock import ensure_utc


def _iso_utc_z(value: datetime) -> str:
    aware = ensure_utc(value)
    assert aware is not None
    return aware.isoformat(timespec="milliseconds").replace("+00:00", "Z")


# ISO-8601 UTC with a trailing Z on the wire (docs/api.md), regardless of the DB backend.
UtcDatetime = Annotated[datetime, PlainSerializer(_iso_utc_z, return_type=str, when_used="json")]


def normalise_email_address(value: str) -> str:
    """Lowercase/trim and require ``local@domain.tld``; raises ``ValueError`` for pydantic."""
    value = value.strip().lower()
    local, sep, domain = value.partition("@")
    if not sep or not local or not domain or "." not in domain:
        raise ValueError("must be a valid email address")
    return value


class ApiModel(BaseModel):
    model_config = ConfigDict(from_attributes=True)


class ErrorBody(BaseModel):
    code: str
    message: str


class ErrorEnvelope(BaseModel):
    error: ErrorBody
