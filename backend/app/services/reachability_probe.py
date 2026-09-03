"""TCP reachability probe: connect, measure, close. Not one byte is sent."""

import asyncio
import ipaddress
import time
from dataclasses import dataclass

PROBE_TIMEOUT_SECONDS = 3.0


@dataclass(frozen=True, slots=True)
class ProbeResult:
    reachable: bool
    latency_ms: int | None


def forbidden_target_reason(ip: str) -> str | None:
    """Return why ``ip`` must not be probed (private/loopback/etc.), or None if it is allowed.

    IPv4-mapped, 6to4 and Teredo IPv6 addresses are checked by their embedded IPv4."""
    addr = ipaddress.ip_address(ip)
    if isinstance(addr, ipaddress.IPv6Address):
        embedded = addr.ipv4_mapped or addr.sixtofour or (addr.teredo[1] if addr.teredo else None)
        if embedded is not None:
            inner = forbidden_target_reason(str(embedded))
            if inner is not None:
                return f"embedded IPv4 is {inner}"
    checks = (
        ("unspecified", addr.is_unspecified),
        ("loopback", addr.is_loopback),
        ("link-local", addr.is_link_local),
        ("multicast", addr.is_multicast),
        ("reserved", addr.is_reserved),
        ("private", addr.is_private),
        ("not globally routable", not addr.is_global),
    )
    for reason, hit in checks:
        if hit:
            return reason
    return None


async def tcp_probe(ip: str, port: int, timeout: float = PROBE_TIMEOUT_SECONDS) -> ProbeResult:
    start = time.perf_counter()
    try:
        _, writer = await asyncio.wait_for(asyncio.open_connection(ip, port), timeout)
    except (TimeoutError, OSError):
        return ProbeResult(reachable=False, latency_ms=None)
    latency_ms = round((time.perf_counter() - start) * 1000)
    writer.close()
    try:
        await writer.wait_closed()
    except OSError:
        pass
    return ProbeResult(reachable=True, latency_ms=latency_ms)
