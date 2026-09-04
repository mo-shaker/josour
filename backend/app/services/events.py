"""Tiny in-process async event bus (publish/subscribe).

This is the seam between the REST layer and the WebSocket layer: request
handlers publish a domain event *after* their transaction is committed, and the WebSocket
layer subscribes to turn events into frames from docs/ws-protocol.md section 4
(``allowlist.updated``, ``session.terminate``, ``hosts.update``). The bus is process-local by
design: the API is deployed as a single uvicorn worker.
"""

import inspect
import logging
import uuid
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from datetime import datetime

log = logging.getLogger(__name__)


@dataclass(frozen=True, slots=True)
class Event:
    """Base class for everything published on the bus."""


@dataclass(frozen=True, slots=True)
class AllowlistPublished(Event):
    """A new ``allowlist_versions`` row has been committed.

    Week 3: broadcast ``allowlist.updated {version}`` to every connected client."""

    version: int
    entries: tuple[str, ...]
    updated_at: datetime


@dataclass(frozen=True, slots=True)
class SessionEnded(Event):
    """A session transitioned to ``ended`` and its ``session_keys`` row was deleted.

    Week 3: send ``session.terminate {session_id, reason}`` to both devices (or the remaining
    one) and broadcast ``hosts.update`` since the host becomes available again."""

    session_id: uuid.UUID
    guest_user_id: uuid.UUID
    guest_device_id: uuid.UUID
    host_user_id: uuid.UUID
    host_device_id: uuid.UUID
    reason: str
    """A ``SessionEndReason`` value."""
    ended_at: datetime


type Handler[E: Event] = Callable[[E], Awaitable[None] | None]
type Unsubscribe = Callable[[], None]


class EventBus:
    """Handlers are matched with ``isinstance`` and awaited sequentially in subscription order.
    A failing handler is logged and skipped so that it can never break the publishing request."""

    def __init__(self) -> None:
        self._subscribers: list[tuple[type[Event], Handler[Event]]] = []

    def subscribe[E: Event](self, event_type: type[E], handler: Handler[E]) -> Unsubscribe:
        """Idempotent: subscribing the same handler twice registers it once."""
        entry = (event_type, handler)
        if entry not in self._subscribers:
            self._subscribers.append(entry)  # type: ignore[arg-type]

        def unsubscribe() -> None:
            try:
                self._subscribers.remove(entry)  # type: ignore[arg-type]
            except ValueError:
                pass

        return unsubscribe

    async def publish(self, event: Event) -> int:
        """Deliver ``event``; returns the number of handlers that completed without error."""
        delivered = 0
        for event_type, handler in list(self._subscribers):
            if not isinstance(event, event_type):
                continue
            try:
                result = handler(event)
                if inspect.isawaitable(result):
                    await result
            except Exception:
                log.exception(
                    "event handler failed",
                    extra={
                        "event": type(event).__name__,
                        "handler": getattr(handler, "__qualname__", repr(handler)),
                    },
                )
            else:
                delivered += 1
        return delivered

    def subscriber_count(self, event_type: type[Event] | None = None) -> int:
        if event_type is None:
            return len(self._subscribers)
        return sum(1 for registered, _ in self._subscribers if registered is event_type)

    def clear(self) -> None:
        self._subscribers.clear()


event_bus = EventBus()
"""Process-wide bus used by the routers; the WebSocket layer subscribes to this instance."""
