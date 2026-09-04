import logging
import uuid

import pytest

from app.core.clock import utcnow
from app.services.events import AllowlistPublished, Event, EventBus, SessionEnded, event_bus
from app.ws import subscribers


def _allowlist_event(version: int = 1) -> AllowlistPublished:
    return AllowlistPublished(version=version, entries=("example.com",), updated_at=utcnow())


def _session_event() -> SessionEnded:
    return SessionEnded(
        session_id=uuid.uuid4(),
        guest_user_id=uuid.uuid4(),
        guest_device_id=uuid.uuid4(),
        host_user_id=uuid.uuid4(),
        host_device_id=uuid.uuid4(),
        reason="admin_terminated",
        ended_at=utcnow(),
    )


async def test_publish_delivers_to_matching_subscribers_only() -> None:
    bus = EventBus()
    seen: list[Event] = []

    async def async_handler(event: AllowlistPublished) -> None:
        seen.append(event)

    def sync_handler(event: AllowlistPublished) -> None:
        seen.append(event)

    bus.subscribe(AllowlistPublished, async_handler)
    bus.subscribe(AllowlistPublished, sync_handler)
    bus.subscribe(SessionEnded, lambda e: seen.append(e))

    event = _allowlist_event()
    assert await bus.publish(event) == 2
    assert seen == [event, event]

    ended = _session_event()
    assert await bus.publish(ended) == 1
    assert seen[-1] is ended


async def test_subscribe_is_idempotent_and_unsubscribe_works() -> None:
    bus = EventBus()
    calls: list[int] = []

    def handler(event: AllowlistPublished) -> None:
        calls.append(event.version)

    unsubscribe = bus.subscribe(AllowlistPublished, handler)
    bus.subscribe(AllowlistPublished, handler)
    assert bus.subscriber_count(AllowlistPublished) == 1

    await bus.publish(_allowlist_event(7))
    assert calls == [7]

    unsubscribe()
    unsubscribe()  # second call is a no-op
    assert bus.subscriber_count() == 0
    await bus.publish(_allowlist_event(8))
    assert calls == [7]


async def test_failing_handler_is_logged_and_does_not_block_others(
    caplog: pytest.LogCaptureFixture,
) -> None:
    bus = EventBus()
    delivered: list[int] = []

    async def broken(_: AllowlistPublished) -> None:
        raise RuntimeError("boom")

    bus.subscribe(AllowlistPublished, broken)
    bus.subscribe(AllowlistPublished, lambda e: delivered.append(e.version))

    with caplog.at_level(logging.ERROR, logger="app.services.events"):
        assert await bus.publish(_allowlist_event(3)) == 1
    assert delivered == [3]
    assert any("event handler failed" in record.message for record in caplog.records)


async def test_base_event_subscriber_receives_everything() -> None:
    bus = EventBus()
    seen: list[str] = []
    bus.subscribe(Event, lambda e: seen.append(type(e).__name__))
    await bus.publish(_allowlist_event())
    await bus.publish(_session_event())
    assert seen == ["AllowlistPublished", "SessionEnded"]


async def test_week3_placeholder_subscribers_are_registered_on_the_global_bus(app) -> None:
    """create_app registers the WebSocket-layer hooks exactly once, however often it runs."""
    assert (AllowlistPublished, subscribers.on_allowlist_published) in event_bus._subscribers
    assert (SessionEnded, subscribers.on_session_ended) in event_bus._subscribers
    assert event_bus.subscriber_count(AllowlistPublished) == 1
    assert event_bus.subscriber_count(SessionEnded) == 1
    # The no-op handlers accept the events without error.
    assert await event_bus.publish(_allowlist_event()) >= 1
    assert await event_bus.publish(_session_event()) >= 1
