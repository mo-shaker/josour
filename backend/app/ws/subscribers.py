"""Event-bus subscribers owned by the WebSocket layer.

Week 2 registers no-op handlers so the seam is exercised end to end; week 3 replaces the bodies
with ConnectionManager broadcasts:

- ``AllowlistPublished`` -> ``allowlist.updated {version}`` to every connected client.
- ``SessionEnded``       -> ``session.terminate {session_id, reason}`` to both devices, then a
  ``hosts.update`` broadcast because the host becomes available again.
"""

import logging

from app.services.events import AllowlistPublished, EventBus, SessionEnded

log = logging.getLogger(__name__)


async def on_allowlist_published(event: AllowlistPublished) -> None:
    log.debug("allowlist published", extra={"version": event.version})


async def on_session_ended(event: SessionEnded) -> None:
    log.debug("session ended", extra={"session_id": str(event.session_id), "reason": event.reason})


def register_subscribers(bus: EventBus) -> None:
    """Idempotent; called from ``create_app``."""
    bus.subscribe(AllowlistPublished, on_allowlist_published)
    bus.subscribe(SessionEnded, on_session_ended)
