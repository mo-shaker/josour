"""UTC time helpers. SQLite drops tzinfo, so values read back are normalised here."""

from datetime import UTC, datetime


def utcnow() -> datetime:
    return datetime.now(UTC)


def ensure_utc(value: datetime | None) -> datetime | None:
    """Return an aware UTC datetime; naive values (SQLite) are assumed to already be UTC."""
    if value is None:
        return None
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
