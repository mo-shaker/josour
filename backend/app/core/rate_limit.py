"""In-process token-bucket rate limiter keyed by an opaque string (the client IP).

The API runs as a single uvicorn worker (Dockerfile), so process-local state is authoritative;
this is deliberately not shared across processes or hosts.
"""

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
