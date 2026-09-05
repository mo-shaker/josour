"""Allowlist entries and immutable published versions.

Validation rules live in ``validate_entry`` and are shared by the CLI (``add-domain``) and
``PUT /admin/domains``. Publishing is two steps: a service call that flushes the new
``allowlist_versions`` row, then - after the caller commits - ``published_event`` on the event
bus, which the WebSocket layer turns into ``allowlist.updated`` (see app.ws.subscribers).
"""

import ipaddress
import re
import uuid
from collections.abc import Iterable, Sequence
from dataclasses import dataclass

from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import ensure_utc
from app.core.errors import Conflict, ValidationFailed
from app.models import AllowedDomain, AllowlistVersion
from app.services.events import AllowlistPublished

_LABEL_RE = re.compile(r"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")

LOOPBACK_NAME = "localhost"
"""RFC 6761 section 6.3: ``localhost`` and anything under it always resolve to loopback.

Product document section 14 forbids reaching the host's localhost, LAN and internal addresses.
The client is what enforces that, after DNS resolution (``IpRangePolicy``), because any public
name can still point at a private address. This is the server's narrow half: the allow-list it
publishes must not *invite* the client to the one name that is loopback by definition. Other
locally-resolving names are deliberately still accepted - ``docs/api.md`` documents
``portal.corp:8443`` as a valid entry, so refusing them would change the REST contract; see
docs/security-review-server.md."""


def validate_entry(raw: str) -> str:
    """Validate one allowlist entry per docs/protocol.md section 6 rule 3.

    Accepted forms: ``example.com`` (with subdomains), ``=exact.com``, either with an optional
    ``:port`` (1-65535). Entries must be lowercase, without ``*``, ``/`` or whitespace, must not
    be an IP address, and must not be :data:`LOOPBACK_NAME` or a name under it."""
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
    labels = host.split(".")
    if not all(_LABEL_RE.match(label) for label in labels):
        raise ValidationFailed("host must be a valid domain name")
    if labels[-1] == LOOPBACK_NAME:
        raise ValidationFailed("'localhost' always resolves to the host's own machine")
    return entry


@dataclass(frozen=True, slots=True)
class EntryError:
    entry: str
    reason: str


def validate_entries(raw_entries: Iterable[str]) -> tuple[list[str], list[EntryError]]:
    """Validate every entry with ``validate_entry`` and collect *all* problems instead of
    stopping at the first. Duplicates (after normalisation) are reported as errors too."""
    entries: list[str] = []
    errors: list[EntryError] = []
    seen: set[str] = set()
    for raw in raw_entries:
        try:
            entry = validate_entry(raw)
        except ValidationFailed as exc:
            errors.append(EntryError(entry=raw, reason=exc.message))
            continue
        if entry in seen:
            errors.append(EntryError(entry=raw, reason="duplicate entry"))
            continue
        seen.add(entry)
        entries.append(entry)
    return entries, errors


def format_entry_errors(errors: Sequence[EntryError]) -> str:
    return "invalid entries: " + "; ".join(f"{e.entry!r} ({e.reason})" for e in errors)


async def get_latest_version(db: AsyncSession) -> AllowlistVersion | None:
    return await db.scalar(
        select(AllowlistVersion).order_by(AllowlistVersion.version.desc()).limit(1)
    )


async def current_version(db: AsyncSession) -> int:
    """The published version number, or 0 when nothing has been published yet. Used by
    ``hello.ack``, ``request.incoming`` and ``session.created``."""
    return await db.scalar(select(func.max(AllowlistVersion.version))) or 0


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
    db: AsyncSession,
    created_by: uuid.UUID | None = None,
    entries: Sequence[str] | None = None,
) -> AllowlistVersion:
    """Insert a new allowlist_versions row (flushed, not committed).

    ``entries`` defaults to a snapshot of the active rows; ``replace_entries`` passes the
    request order explicitly so the published version matches what the admin sent."""
    current = await db.scalar(select(func.max(AllowlistVersion.version)))
    snapshot = AllowlistVersion(
        version=(current or 0) + 1,
        entries=list(entries) if entries is not None else await list_active_entries(db),
        created_by=created_by,
    )
    db.add(snapshot)
    await db.flush()
    return snapshot


def published_event(version: AllowlistVersion) -> AllowlistPublished:
    """Event to publish on the bus once the version's transaction is committed."""
    updated_at = ensure_utc(version.created_at)
    assert updated_at is not None
    return AllowlistPublished(
        version=version.version, entries=tuple(version.entries), updated_at=updated_at
    )


async def replace_entries(
    db: AsyncSession, raw_entries: Iterable[str], *, created_by: uuid.UUID | None = None
) -> AllowlistVersion:
    """Full replacement (``PUT /admin/domains``): every entry is validated first and the whole
    request is rejected if any is invalid. Entries absent from the new list are deactivated,
    known ones re-activated, new ones inserted; then a version is published. Callers commit."""
    entries, errors = validate_entries(raw_entries)
    if errors:
        raise ValidationFailed(format_entry_errors(errors))
    existing = {row.entry: row for row in await db.scalars(select(AllowedDomain))}
    wanted = set(entries)
    for entry, row in existing.items():
        active = entry in wanted
        if row.is_active != active:
            row.is_active = active
    for entry in entries:
        if entry not in existing:
            db.add(AllowedDomain(entry=entry))
    await db.flush()
    return await publish_version(db, created_by, entries=entries)


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
