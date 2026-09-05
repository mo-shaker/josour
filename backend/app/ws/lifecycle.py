"""Process start/stop for the WebSocket layer, called from the application lifespan.

The registry is in memory, so a restart means nothing that claimed to be live can still be live:
every presence row is reset and every unfinished session is ended before the first client can
reconnect. On the way out, clients are closed with 1012 so they reconnect with backoff.
"""

import logging

from app.core.config import Settings, get_settings
from app.db.session import session_scope
from app.services import device_risk, presence, session_flow
from app.services import sessions as session_service
from app.services.events import event_bus
from app.services.session_timer import scheduler
from app.ws import notify
from app.ws.connection_manager import connection_manager
from app.ws.protocol import CloseCode

log = logging.getLogger(__name__)


async def on_startup(settings: Settings | None = None) -> None:
    settings = settings or get_settings()
    scheduler.cancel_all()
    connection_manager.reset()
    notify.reset_broadcast_state()
    async with session_scope() as db:
        rows = await presence.reset_all(db)
        events = await session_service.end_dangling_sessions(db)
        await db.commit()
        # The sweep above ends every non-ended session, so this normally arms nothing. It is
        # what keeps the scheduler and the sweep in agreement: a session that is live in the
        # database is a session with timers, and there is no third possibility.
        rearmed = await session_flow.reschedule_timers(db)
    for event in events:
        await event_bus.publish(event)
    # Suspicious-device detection rides the same scheduler as the session deadlines (ADR-0008),
    # so it is armed after the reset above and torn down by the same ``cancel_all``.
    armed = device_risk.start(settings)
    log.info(
        "ws state reset",
        extra={
            "presence_rows": rows,
            "sessions_ended": len(events),
            "timers_rearmed": rearmed,
            "device_risk_sweep": armed,
        },
    )


async def on_shutdown() -> None:
    closed = await connection_manager.close_all(CloseCode.SERVER_RESTART)
    connection_manager.reset()
    notify.reset_broadcast_state()
    timers = scheduler.cancel_all()
    log.info("ws shutdown", extra={"connections": closed, "timers": timers})
