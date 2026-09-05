"""A tiny keyed asyncio scheduler for the server-side deadlines of docs/ws-protocol.md.

Keys are namespaced strings so the different users cannot collide: ``request:<uuid>`` for the
pending-request timeout, ``probe:<uuid>`` for the reachability probe, and
``session-connect:<uuid>`` / ``session-expiry:<uuid>`` for the two session deadlines of section 5
(``app.services.sessions`` builds the last two and cancels them in ``end_session``).

Scheduling the same key twice cancels the first timer - the last schedule wins.
"""

import asyncio
import logging
from collections.abc import Awaitable, Callable
from datetime import datetime

from app.core.clock import ensure_utc, utcnow

log = logging.getLogger(__name__)

type TimerCallback = Callable[[], Awaitable[None]]


class TimerScheduler:
    def __init__(self) -> None:
        self._timers: dict[str, asyncio.Task[None]] = {}

    def schedule_after(self, key: str, delay: float, callback: TimerCallback) -> None:
        """Run ``callback`` in ``delay`` seconds (immediately when it is <= 0)."""
        self.cancel(key)
        task = asyncio.create_task(self._run(key, max(delay, 0.0), callback), name=f"timer:{key}")
        self._timers[key] = task

    def schedule_at(self, key: str, when: datetime, callback: TimerCallback) -> None:
        """Run ``callback`` at ``when`` (UTC); a past deadline fires on the next loop pass."""
        deadline = ensure_utc(when)
        assert deadline is not None
        self.schedule_after(key, (deadline - utcnow()).total_seconds(), callback)

    def cancel(self, key: str) -> bool:
        task = self._timers.pop(key, None)
        if task is None:
            return False
        task.cancel()
        return True

    def cancel_all(self) -> int:
        """Shutdown path; also used between tests."""
        tasks = list(self._timers.values())
        self._timers.clear()
        for task in tasks:
            task.cancel()
        return len(tasks)

    def is_scheduled(self, key: str) -> bool:
        task = self._timers.get(key)
        return task is not None and not task.done()

    def __len__(self) -> int:
        return len(self._timers)

    async def _run(self, key: str, delay: float, callback: TimerCallback) -> None:
        try:
            if delay > 0:
                await asyncio.sleep(delay)
        except asyncio.CancelledError:
            raise
        # Drop the entry first so the callback may re-schedule the same key.
        if self._timers.get(key) is asyncio.current_task():
            del self._timers[key]
        try:
            await callback()
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("scheduled timer failed", extra={"timer": key})


scheduler = TimerScheduler()
"""Process-wide scheduler; reset by the application lifespan."""
