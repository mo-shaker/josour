"""Event-bus subscribers owned by the WebSocket layer.

The REST layer (and the WebSocket layer itself) publishes domain events after committing; this
module is the single place that turns them into frames from docs/ws-protocol.md section 4:

- ``AllowlistPublished`` -> ``allowlist.updated {version}`` to every connected client.
- ``SessionEnded``       -> ``session.terminate {session_id, reason}`` to both devices, then a
  ``hosts.update`` broadcast because the host becomes available again.
"""

import logging

from app.db.session import session_scope
from app.services.events import AllowlistPublished, EventBus, SessionEnded
from app.ws import notify
from app.ws.connection_manager import connection_manager
from app.ws.protocol import AllowlistUpdated, SessionTerminate

log = logging.getLogger(__name__)


async def on_allowlist_published(event: AllowlistPublished) -> None:
    delivered = await connection_manager.broadcast(AllowlistUpdated(version=event.version))
    log.info("allowlist published", extra={"version": event.version, "clients": delivered})


async def on_session_ended(event: SessionEnded) -> None:
    """Both peers are told; whichever one caused the end is simply no longer connected."""
    message = SessionTerminate(session_id=event.session_id, reason=event.reason)
    for device_id in (event.guest_device_id, event.host_device_id):
        await notify.send_to_device(device_id, message)
    log.info("session ended", extra={"session_id": str(event.session_id), "reason": event.reason})
    if not connection_manager.connections():
        return
    async with session_scope() as db:
        await notify.broadcast_hosts_update(db)


def register_subscribers(bus: EventBus) -> None:
    """Idempotent; called from ``create_app``."""
    bus.subscribe(AllowlistPublished, on_allowlist_published)
    bus.subscribe(SessionEnded, on_session_ended)
