"""Allowlist entries and immutable published versions."""

import ipaddress
import re
import uuid

from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.errors import Conflict, ValidationFailed
from app.models import AllowedDomain, AllowlistVersion

_LABEL_RE = re.compile(r"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")


def validate_entry(raw: str) -> str:
    """Validate one allowlist entry per docs/protocol.md section 6 rule 3.

    Accepted forms: ``example.com`` (with subdomains), ``=exact.com``, either with an optional
    ``:port`` (1-65535). Entries must be lowercase, without ``*``, ``/`` or whitespace."""
    entry = raw.strip()
    if not entry:
        raise ValidationFailed("entry must not be empty")
    if any(ch.isspace() for ch in entry):
        raise ValidationFailed("entry must not contain whitespace")
    if "*" in entry or "/" in entry:
        raise ValidationFailed("entry must not contain '*' or '/'")
    if entry != entry.lower():
        raise ValidationFailed("entry must be lowercase")

    body = entry[1:] if entry.startswith("=") else entry
    host, sep, port = body.rpartition(":")
    if not sep:
        host, port = body, ""
    if sep:
        if not port.isdigit() or not 1 <= int(port) <= 65535:
            raise ValidationFailed("port must be an integer between 1 and 65535")
        if port != str(int(port)):
            raise ValidationFailed("port must not have leading zeros")
    if not host or len(host) > 253:
        raise ValidationFailed("host must be 1-253 characters")
    try:
        ipaddress.ip_address(host)
    except ValueError:
        pass
    else:
        raise ValidationFailed("host must be a domain name, not an IP address")
    if not all(_LABEL_RE.match(label) for label in host.split(".")):
        raise ValidationFailed("host must be a valid domain name")
    return entry


async def get_latest_version(db: AsyncSession) -> AllowlistVersion | None:
    return await db.scalar(
        select(AllowlistVersion).order_by(AllowlistVersion.version.desc()).limit(1)
    )


async def get_version(db: AsyncSession, version: int) -> AllowlistVersion | None:
    return await db.get(AllowlistVersion, version)


async def list_active_entries(db: AsyncSession) -> list[str]:
    rows = await db.scalars(
        select(AllowedDomain.entry)
        .where(AllowedDomain.is_active.is_(True))
        .order_by(AllowedDomain.updated_at, AllowedDomain.entry)
    )
    return list(rows)


async def publish_version(
    db: AsyncSession, created_by: uuid.UUID | None = None
) -> AllowlistVersion:
    """Snapshot the active entries into a new allowlist_versions row (flushed, not committed)."""
    current = await db.scalar(select(func.max(AllowlistVersion.version)))
    snapshot = AllowlistVersion(
        version=(current or 0) + 1,
        entries=await list_active_entries(db),
        created_by=created_by,
    )
    db.add(snapshot)
    await db.flush()
    # TODO(week 5): broadcast allowlist.updated {version} to all WebSocket clients.
    return snapshot


async def add_domain(
    db: AsyncSession,
    raw_entry: str,
    *,
    created_by: uuid.UUID | None = None,
    note: str | None = None,
) -> AllowlistVersion:
    """Add (or re-activate) an entry and publish a new version. Callers commit."""
    entry = validate_entry(raw_entry)
    existing = await db.scalar(select(AllowedDomain).where(AllowedDomain.entry == entry))
    if existing is None:
        db.add(AllowedDomain(entry=entry, note=note))
    elif existing.is_active:
        raise Conflict(f"'{entry}' is already in the allowlist")
    else:
        existing.is_active = True
        existing.note = note or existing.note
    await db.flush()
    return await publish_version(db, created_by)
