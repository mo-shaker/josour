"""In-process token-bucket rate limiters keyed by an opaque string.

The API runs as a single uvicorn worker (Dockerfile), so process-local state is authoritative;
this is deliberately not shared across processes or hosts.

The policy the four buckets implement - which endpoint, which key, which numbers, and why they
do not fight the account lockout - is ADR-0008 (docs/decisions/0008-rate-limit-policy.md).
"""

import hashlib
import math
import time
from collections.abc import Callable
from dataclasses import dataclass


@dataclass(slots=True)
class _Bucket:
    tokens: float
    updated: float


@dataclass(frozen=True, slots=True)
class RateLimitDecision:
    allowed: bool
    retry_after_seconds: int
    """Whole seconds until the next request would be accepted; 0 when ``allowed``."""


class TokenBucketLimiter:
    """Allows a burst of ``capacity`` requests, refilled continuously at ``capacity`` per
    ``period_seconds`` (so "5 per minute" is ``TokenBucketLimiter(5, 60)``)."""

    def __init__(
        self,
        capacity: int,
        period_seconds: float = 60.0,
        *,
        clock: Callable[[], float] = time.monotonic,
        max_keys: int = 10_000,
    ) -> None:
        if capacity < 1 or period_seconds <= 0:
            raise ValueError("capacity must be >= 1 and period_seconds > 0")
        self.capacity = capacity
        self.refill_per_second = capacity / period_seconds
        self._clock = clock
        self._max_keys = max_keys
        self._buckets: dict[str, _Bucket] = {}

    def check(self, key: str) -> RateLimitDecision:
        """Consume one token for ``key`` if available."""
        now = self._clock()
        bucket = self._buckets.get(key)
        if bucket is None:
            if len(self._buckets) >= self._max_keys:
                self._prune(now)
            bucket = _Bucket(tokens=float(self.capacity), updated=now)
            self._buckets[key] = bucket
        else:
            self._refill(bucket, now)
        if bucket.tokens >= 1.0:
            bucket.tokens -= 1.0
            return RateLimitDecision(allowed=True, retry_after_seconds=0)
        wait = (1.0 - bucket.tokens) / self.refill_per_second
        return RateLimitDecision(allowed=False, retry_after_seconds=max(1, math.ceil(wait)))

    def refund(self, key: str) -> None:
        """Give one token back, never exceeding ``capacity``.

        Used by ``POST /auth/login`` on the per-email bucket after a *successful* login, so that
        only failed attempts durably spend the budget (ADR-0008). A refund for a key that was
        never checked is a no-op rather than a new full bucket."""
        bucket = self._buckets.get(key)
        if bucket is None:
            return
        self._refill(bucket, self._clock())
        bucket.tokens = min(float(self.capacity), bucket.tokens + 1.0)

    def reset(self) -> None:
        self._buckets.clear()

    def _refill(self, bucket: _Bucket, now: float) -> None:
        elapsed = max(0.0, now - bucket.updated)
        bucket.tokens = min(float(self.capacity), bucket.tokens + elapsed * self.refill_per_second)
        bucket.updated = now

    def _prune(self, now: float) -> None:
        """Drop idle (fully refilled) buckets; if still over the cap, drop the oldest half."""
        for key, bucket in list(self._buckets.items()):
            self._refill(bucket, now)
            if bucket.tokens >= self.capacity:
                del self._buckets[key]
        if len(self._buckets) >= self._max_keys:
            oldest = sorted(self._buckets, key=lambda k: self._buckets[k].updated)
            for key in oldest[: len(oldest) // 2 + 1]:
                del self._buckets[key]


def opaque_key(value: str) -> str:
    """A fixed-length bucket key for a caller-supplied, personally identifying string.

    The submitted email keys one of the login buckets (ADR-0008). Hashing it bounds the key
    length (the input is attacker-controlled and up to 320 characters) and keeps a table of real
    addresses out of process memory, which is the same reasoning as privacy item 15.x elsewhere.
    """
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


@dataclass(frozen=True, slots=True)
class RateLimiters:
    """The four buckets of ADR-0008, built once per application.

    ``None`` in ``app.state`` (``RATE_LIMIT_ENABLED=false``) disables all of them together; there
    is deliberately no way to disable one and not the others, so the load harness and the
    test-suite cannot end up exercising a half-limited server.
    """

    login_ip: TokenBucketLimiter
    """``POST /auth/login`` per client IP."""
    login_email: TokenBucketLimiter
    """``POST /auth/login`` per submitted email (hashed with :func:`opaque_key`)."""
    refresh_ip: TokenBucketLimiter
    """``POST /auth/refresh`` per client IP."""
    probe_user: TokenBucketLimiter
    """``POST /probe`` per authenticated user."""

    def reset(self) -> None:
        for limiter in (self.login_ip, self.login_email, self.refresh_ip, self.probe_user):
            limiter.reset()
