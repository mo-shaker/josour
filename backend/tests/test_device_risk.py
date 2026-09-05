"""Automatic revocation of a suspicious device (ADR-0008, part two).

Every rule is pinned from both sides: the pattern fires, and the near miss does not. The near
misses matter more than the hits here - a false revocation cuts a real user off their own
machine, so the thresholds are the safety property being tested.
"""

import uuid
from collections.abc import Awaitable, Callable
from datetime import timedelta
from typing import Any

import pytest
from httpx import AsyncClient
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from app.core.clock import utcnow
from app.core.config import Settings
from app.models import Device, RefreshToken, SecurityEvent
from app.models.enums import DeviceStatus, SecurityEventType
from app.services import device_risk
from app.services.session_timer import scheduler
from app.services.tokens import issue_refresh_token
from tests.conftest import Actor, ActorFactory, WsFactory, make_settings

EventFactory = Callable[..., Awaitable[SecurityEvent]]


@pytest.fixture
def settings() -> Settings:
    return make_settings()


def _event_factory(db: AsyncSession) -> EventFactory:
    """Insert one audit row, optionally back-dated out of the detection window."""

    async def build(
        actor: Actor,
        event_type: SecurityEventType,
        *,
        reason: str | None = None,
        minutes_ago: float = 0,
        device_id: uuid.UUID | None = None,
    ) -> SecurityEvent:
        event = SecurityEvent(
            type=event_type,
            user_id=actor.user.id,
            device_id=device_id if device_id is not None else actor.device.id,
            ip="203.0.113.5",
            details={"reason": reason} if reason else None,
            created_at=utcnow() - timedelta(minutes=minutes_ago),
        )
        db.add(event)
        await db.commit()
        return event

    return build


async def _secret_failures(add_event: EventFactory, actor: Actor, count: int, **kw: Any) -> None:
    for _ in range(count):
        await add_event(actor, SecurityEventType.LOGIN_FAILED, reason="device_secret_invalid", **kw)


async def _status(db: AsyncSession, actor: Actor) -> DeviceStatus:
    device = await db.get(Device, actor.device.id)
    assert device is not None
    await db.refresh(device)
    return device.status


async def _revocation_rows(db: AsyncSession, actor: Actor) -> list[SecurityEvent]:
    return list(
        await db.scalars(
            select(SecurityEvent).where(
                SecurityEvent.type == SecurityEventType.DEVICE_REVOKED,
                SecurityEvent.device_id == actor.device.id,
            )
        )
    )


# ------------------------------------------------------------ rule A: repeated bad device secret


async def test_repeated_device_secret_failures_revoke_the_device(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    actor = await make_actor("thief@example.com")
    await _secret_failures(_event_factory(db), actor, settings.device_risk_secret_failures)

    revoked = await device_risk.evaluate(db, settings)

    assert [d.device_id for d in revoked] == [actor.device.id]
    assert revoked[0].rule == "device_secret_invalid"
    assert await _status(db, actor) == DeviceStatus.REVOKED


async def test_one_failure_below_the_threshold_does_not(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    actor = await make_actor("clumsy@example.com")
    await _secret_failures(_event_factory(db), actor, settings.device_risk_secret_failures - 1)

    assert await device_risk.evaluate(db, settings) == []
    assert await _status(db, actor) == DeviceStatus.ACTIVE


async def test_a_successful_login_inside_the_window_clears_the_suspicion(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    """The owner still holds the real secret, so the failures were noise, not impersonation."""
    actor = await make_actor("noisy@example.com")
    add = _event_factory(db)
    await _secret_failures(add, actor, settings.device_risk_secret_failures * 2)
    await add(actor, SecurityEventType.LOGIN_SUCCESS)

    assert await device_risk.evaluate(db, settings) == []
    assert await _status(db, actor) == DeviceStatus.ACTIVE


async def test_events_older_than_the_window_do_not_count(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    actor = await make_actor("old@example.com")
    add = _event_factory(db)
    stale = settings.device_risk_window_minutes + 1
    await _secret_failures(add, actor, settings.device_risk_secret_failures, minutes_ago=stale)
    assert await device_risk.evaluate(db, settings) == []

    # ...and one fresh burst on top of the stale one does fire.
    await _secret_failures(add, actor, settings.device_risk_secret_failures)
    assert len(await device_risk.evaluate(db, settings)) == 1


async def test_failures_are_counted_per_device_not_per_user(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    """Two devices of the same user, half the failures each: neither reaches the threshold."""
    actor = await make_actor("split@example.com")
    second = Device(
        user_id=actor.user.id, name="SECOND-PC", os_version="Windows 11", device_secret_hash="x"
    )
    db.add(second)
    await db.commit()
    add = _event_factory(db)
    half = settings.device_risk_secret_failures - 1
    await _secret_failures(add, actor, half)
    await _secret_failures(add, actor, half, device_id=second.id)

    assert await device_risk.evaluate(db, settings) == []


# ------------------------------------------------------------------- rule B: refresh token reuse


async def test_repeated_refresh_reuse_revokes_the_device(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    actor = await make_actor("stolen@example.com")
    add = _event_factory(db)
    for _ in range(settings.device_risk_refresh_reuse):
        await add(actor, SecurityEventType.REFRESH_REUSE)

    revoked = await device_risk.evaluate(db, settings)

    assert [d.rule for d in revoked] == ["refresh_reuse"]
    assert revoked[0].matches == settings.device_risk_refresh_reuse
    assert await _status(db, actor) == DeviceStatus.REVOKED


async def test_a_single_reuse_race_does_not_revoke(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    """The first reuse kills the whole chain, so one or two in-flight tokens can legitimately
    bounce back afterwards. Only a presenter that keeps returning is treated as theft."""
    actor = await make_actor("racy@example.com")
    add = _event_factory(db)
    for _ in range(settings.device_risk_refresh_reuse - 1):
        await add(actor, SecurityEventType.REFRESH_REUSE)

    assert await device_risk.evaluate(db, settings) == []
    assert await _status(db, actor) == DeviceStatus.ACTIVE


# ------------------------------------------------------- what a revocation does, and does twice


async def test_revocation_writes_an_audit_row_explaining_the_trigger(
    db: AsyncSession,
    make_actor: ActorFactory,
    settings: Settings,
    client: AsyncClient,
    admin_headers: dict,
) -> None:
    actor = await make_actor("audited@example.com")
    add = _event_factory(db)
    await _secret_failures(add, actor, settings.device_risk_secret_failures + 2)

    await device_risk.evaluate(db, settings)

    rows = await _revocation_rows(db, actor)
    assert len(rows) == 1
    assert rows[0].details == {
        "by": "auto",
        "rule": "device_secret_invalid",
        "matches": settings.device_risk_secret_failures + 2,
        "window_minutes": settings.device_risk_window_minutes,
    }

    # And an administrator can see it through the documented endpoint.
    response = await client.get(
        "/api/v1/admin/security-events?type=device_revoked", headers=admin_headers
    )
    assert response.status_code == 200, response.text
    listed = response.json()
    assert [row["details"]["rule"] for row in listed] == ["device_secret_invalid"]
    assert listed[0]["details"]["by"] == "auto"


async def test_revocation_is_idempotent(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    actor = await make_actor("twice@example.com")
    await _secret_failures(_event_factory(db), actor, settings.device_risk_secret_failures)

    assert len(await device_risk.evaluate(db, settings)) == 1
    # The events are still inside the window on the next pass; the device must not be touched
    # again, and no second audit row may appear.
    assert await device_risk.evaluate(db, settings) == []
    assert len(await _revocation_rows(db, actor)) == 1


async def test_revocation_kills_the_refresh_tokens(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    actor = await make_actor("tokens@example.com")
    await issue_refresh_token(db, settings, actor.user.id, actor.device.id)
    await db.commit()
    await _secret_failures(_event_factory(db), actor, settings.device_risk_secret_failures)

    await device_risk.evaluate(db, settings)

    live = await db.scalar(
        select(func.count())
        .select_from(RefreshToken)
        .where(RefreshToken.device_id == actor.device.id, RefreshToken.revoked_at.is_(None))
    )
    assert live == 0


async def test_the_sweep_closes_a_revoked_devices_live_channel(
    db: AsyncSession,
    make_actor: ActorFactory,
    ws_connect: WsFactory,
    settings: Settings,
) -> None:
    from app.ws.protocol import CloseCode

    actor = await make_actor("live@example.com")
    ws = await ws_connect(actor)
    assert ws.hello_ack is not None
    await _secret_failures(_event_factory(db), actor, settings.device_risk_secret_failures)

    revoked = await device_risk.sweep(settings)

    assert [d.device_id for d in revoked] == [actor.device.id]
    assert await ws.wait_closed() == CloseCode.NOT_ALLOWED


# ------------------------------------------------------------------------ signals we ignore


async def test_listener_unauthenticated_never_revokes_anything(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    """It describes the network around a host, not the host's own behaviour. Acting on it would
    let any port scanner on the internet cut a user off their machine."""
    actor = await make_actor("victim@example.com")
    add = _event_factory(db)
    for _ in range(50):
        await add(actor, SecurityEventType.LISTENER_UNAUTHENTICATED)

    assert await device_risk.evaluate(db, settings) == []
    assert await _status(db, actor) == DeviceStatus.ACTIVE


async def test_bad_password_failures_never_revoke_a_device(
    db: AsyncSession, make_actor: ActorFactory, settings: Settings
) -> None:
    """Password guessing is an attack on an account; the lockout and the email rate limit answer
    it. Revoking devices for it would hand the attacker a device-level denial of service."""
    actor = await make_actor("guessed@example.com")
    add = _event_factory(db)
    for _ in range(settings.device_risk_secret_failures * 3):
        await add(actor, SecurityEventType.LOGIN_FAILED, reason="bad_password")

    assert await device_risk.evaluate(db, settings) == []
    assert await _status(db, actor) == DeviceStatus.ACTIVE


# ------------------------------------------------------------------------------- the off switch


async def test_thresholds_can_be_tuned_and_rules_disabled(
    db: AsyncSession, make_actor: ActorFactory
) -> None:
    actor = await make_actor("tunable@example.com")
    add = _event_factory(db)
    await _secret_failures(add, actor, 3)

    lenient = make_settings(device_risk_secret_failures=99)
    assert await device_risk.evaluate(db, lenient) == []

    off = make_settings(device_risk_secret_failures=0)
    assert await device_risk.evaluate(db, off) == []

    strict = make_settings(device_risk_secret_failures=3)
    assert len(await device_risk.evaluate(db, strict)) == 1


async def test_detection_can_be_disabled_entirely(
    db: AsyncSession, make_actor: ActorFactory
) -> None:
    actor = await make_actor("ignored@example.com")
    await _secret_failures(_event_factory(db), actor, 50)

    disabled = make_settings(device_risk_enabled=False)
    assert await device_risk.evaluate(db, disabled) == []
    assert await _status(db, actor) == DeviceStatus.ACTIVE
    assert device_risk.start(disabled) is False
    assert not scheduler.is_scheduled(device_risk.SWEEP_KEY)


async def test_the_sweep_re_arms_itself_on_the_shared_scheduler(settings: Settings) -> None:
    """No second scheduling mechanism: it is one key on the session-deadline scheduler."""
    assert device_risk.start(settings) is True
    assert scheduler.is_scheduled(device_risk.SWEEP_KEY)
    assert device_risk.stop() is True
    assert not scheduler.is_scheduled(device_risk.SWEEP_KEY)


async def test_the_sweep_re_arms_itself_after_every_pass(
    monkeypatch: pytest.MonkeyPatch, settings: Settings
) -> None:
    """It is a one-shot scheduler, so a pass that forgot to re-arm would silently stop the
    detection for the life of the process."""
    captured: dict[str, tuple[float, Callable[[], Awaitable[None]]]] = {}
    real = scheduler.schedule_after

    def capture(key: str, delay: float, callback: Callable[[], Awaitable[None]]) -> None:
        captured[key] = (delay, callback)
        real(key, delay, callback)

    monkeypatch.setattr(scheduler, "schedule_after", capture)
    assert device_risk.start(settings) is True
    delay, callback = captured[device_risk.SWEEP_KEY]
    assert delay == settings.device_risk_interval_seconds

    captured.clear()
    await callback()
    assert device_risk.SWEEP_KEY in captured, "the pass did not schedule the next one"
    device_risk.stop()


async def test_a_failing_pass_does_not_stop_the_sweep(
    monkeypatch: pytest.MonkeyPatch, settings: Settings
) -> None:
    """A transient database error must cost one pass, not every future one."""
    captured: dict[str, Callable[[], Awaitable[None]]] = {}

    def capture(key: str, delay: float, callback: Callable[[], Awaitable[None]]) -> None:
        captured[key] = callback

    async def explode(_: Settings) -> list[device_risk.Detection]:
        raise RuntimeError("database is away")

    monkeypatch.setattr(scheduler, "schedule_after", capture)
    monkeypatch.setattr(device_risk, "sweep", explode)
    device_risk.start(settings)

    callback = captured.pop(device_risk.SWEEP_KEY)
    await callback()  # must not raise

    assert device_risk.SWEEP_KEY in captured
