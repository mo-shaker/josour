"""Automatic revocation of a suspicious device (ADR-0008, part two; product item 14.18).

The signals have been written to ``security_events`` since week 1 and nothing read them: blocking
a device was either manual (an administrator, or its owner) or indirect (the account lockout, the
refresh-reuse chain revocation). This module closes that gap with a periodic sweep.

It is deliberately timid. The two failure modes are not symmetric:

* revoking a few minutes late costs almost nothing - the damage these patterns describe is
  cumulative, not instantaneous;
* revoking a device that did nothing wrong cuts a real person off their own machine (refresh
  tokens gone, presence cleared, control channel closed with 4403, and a full sign-in that
  registers a *new* device id, so colleagues stop seeing them in the host list).

So every threshold defaults well above what ordinary use produces, every rule can be turned off
on its own (threshold ``0``) and the whole sweep can be turned off (``DEVICE_RISK_ENABLED``), and
a rule only fires on a pattern that has no innocent explanation left. What the rules are and why
those numbers is ADR-0008; this module implements exactly that and nothing more.
"""

import logging
import uuid
from collections import defaultdict
from dataclasses import dataclass
from datetime import timedelta

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.core.config import Settings
from app.db.session import session_scope
from app.models import Device, SecurityEvent
from app.models.enums import DeviceStatus, SecurityEventType
from app.services.devices import revoke_device
from app.services.session_timer import scheduler

log = logging.getLogger(__name__)

SWEEP_KEY = "device-risk-sweep"
"""Key on the existing timer scheduler; there is no second scheduling mechanism in the process."""

MAX_EVENTS_PER_SWEEP = 5000
"""Ceiling on rows read per pass. Under a flood the most recent events win, which is what the
rules are about anyway."""

SECRET_INVALID_REASON = "device_secret_invalid"

_WATCHED_TYPES = (
    SecurityEventType.LOGIN_FAILED,
    SecurityEventType.LOGIN_SUCCESS,
    SecurityEventType.REFRESH_REUSE,
)


@dataclass(frozen=True, slots=True)
class Detection:
    """One device matched one rule. ``details`` is what the audit row will explain itself with."""

    device_id: uuid.UUID
    rule: str
    matches: int

    def details(self, window_minutes: int) -> dict[str, object]:
        return {"rule": self.rule, "matches": self.matches, "window_minutes": window_minutes}


@dataclass(slots=True)
class _Counts:
    secret_invalid: int = 0
    refresh_reuse: int = 0
    login_success: int = 0


async def _recent_counts(db: AsyncSession, window_minutes: int) -> dict[uuid.UUID, _Counts]:
    """Tally the watched events per device inside the window.

    The ``device_secret_invalid`` discriminator lives in the JSON ``details`` column, and this
    schema runs on both PostgreSQL (JSONB) and SQLite, so the filtering is done in Python on a
    bounded, indexed slice rather than in a dialect-specific JSON predicate."""
    since = utcnow() - timedelta(minutes=window_minutes)
    rows = await db.scalars(
        select(SecurityEvent)
        .where(
            SecurityEvent.created_at >= since,
            SecurityEvent.device_id.is_not(None),
            SecurityEvent.type.in_([t.value for t in _WATCHED_TYPES]),
        )
        .order_by(SecurityEvent.created_at.desc())
        .limit(MAX_EVENTS_PER_SWEEP)
    )
    counts: dict[uuid.UUID, _Counts] = defaultdict(_Counts)
    for event in rows:
        assert event.device_id is not None
        tally = counts[event.device_id]
        if event.type == SecurityEventType.REFRESH_REUSE:
            tally.refresh_reuse += 1
        elif event.type == SecurityEventType.LOGIN_SUCCESS:
            tally.login_success += 1
        elif isinstance(event.details, dict):
            if event.details.get("reason") == SECRET_INVALID_REASON:
                tally.secret_invalid += 1
    return counts


def _match(counts: _Counts, settings: Settings) -> tuple[str, int] | None:
    """The two rules of ADR-0008 as ``(rule, matches)``; the first that fires wins."""
    if (
        settings.device_risk_secret_failures
        and counts.secret_invalid >= settings.device_risk_secret_failures
        # A successful sign-in from the same device inside the window proves the real secret is
        # still with its owner, so the failures were noise, not impersonation.
        and counts.login_success == 0
    ):
        return SECRET_INVALID_REASON, counts.secret_invalid
    if (
        settings.device_risk_refresh_reuse
        and counts.refresh_reuse >= settings.device_risk_refresh_reuse
    ):
        return "refresh_reuse", counts.refresh_reuse
    return None


async def detect(db: AsyncSession, settings: Settings) -> list[Detection]:
    """Devices that currently match a rule and are not revoked yet. Reads only."""
    if not settings.device_risk_enabled:
        return []
    counts = await _recent_counts(db, settings.device_risk_window_minutes)
    detections: list[Detection] = []
    for device_id, tally in counts.items():
        match = _match(tally, settings)
        if match is None:
            continue
        device = await db.get(Device, device_id)
        if device is None or device.status == DeviceStatus.REVOKED:
            continue  # already revoked: idempotent, and no second audit row
        rule, matches = match
        detections.append(Detection(device_id=device_id, rule=rule, matches=matches))
    return detections


async def evaluate(db: AsyncSession, settings: Settings) -> list[Detection]:
    """Detect and revoke. Commits; the caller closes the live channels (see :func:`sweep`).

    Every revocation goes through the same ``revoke_device`` the admin API uses, so it revokes
    the refresh tokens, clears presence, and writes a ``device_revoked`` row - here carrying
    ``by: "auto"`` plus the rule and the count that triggered it, visible in
    ``GET /admin/security-events``."""
    detections = await detect(db, settings)
    revoked: list[Detection] = []
    for detection in detections:
        device = await db.get(Device, detection.device_id)
        if device is None:
            continue
        changed = await revoke_device(
            db,
            device,
            by="auto",
            actor_user_id=None,
            ip=None,
            reason=detection.details(settings.device_risk_window_minutes),
        )
        if changed:
            revoked.append(detection)
    if revoked:
        await db.commit()
        log.warning(
            "devices revoked automatically",
            extra={"count": len(revoked), "rules": sorted({d.rule for d in revoked})},
        )
    return revoked


async def sweep(settings: Settings) -> list[Detection]:
    """One pass: evaluate, then drop the control channel of everything revoked."""
    # Imported here rather than at module scope: the services layer stays free of a hard
    # dependency on the WebSocket layer, which imports services itself.
    from app.ws import notify
    from app.ws.protocol import CloseCode

    async with session_scope() as db:
        revoked = await evaluate(db, settings)
    for detection in revoked:
        await notify.close_device(detection.device_id, CloseCode.NOT_ALLOWED)
    return revoked


def start(settings: Settings) -> bool:
    """Arm the periodic sweep on the shared scheduler. ``False`` when it is disabled."""
    if not settings.device_risk_enabled:
        return False

    async def run() -> None:
        try:
            await sweep(settings)
        except Exception:
            # A transient database error must not stop the sweep for the life of the process.
            # Cancellation (shutdown) is a BaseException and deliberately does *not* re-arm.
            log.exception("device risk sweep failed")
        start(settings)

    scheduler.schedule_after(SWEEP_KEY, settings.device_risk_interval_seconds, run)
    return True


def stop() -> bool:
    return scheduler.cancel(SWEEP_KEY)
