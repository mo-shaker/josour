"""A one-way link with delay, for measuring the relay over something other than loopback.

The same reasoning as ``client/tests/Josour.Tunnel.Tests/Perf/LinkSimulator.cs``: measuring a
forwarding hop on loopback with RTT ~ 0 flatters it, because the interesting question is what the
hop costs *relative to the wire it sits on*. This is the smaller cousin of that simulator - the
relay is a transparent TCP pump with no window of its own, so bandwidth shaping and jitter would
not change what it is being asked, and are left out rather than half-modelled.

Arrival times are absolute, not cumulative, so one late wake-up does not push everything after it.
"""

from __future__ import annotations

import asyncio
import time


class DelayedPipe:
    """Forwards ``reader`` to ``writer``, holding every chunk for ``delay`` seconds."""

    def __init__(self, delay: float) -> None:
        self.delay = delay

    async def pump(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        try:
            while True:
                data = await reader.read(65536)
                if not data:
                    break
                if self.delay:
                    # Absolute arrival time: sleep to the deadline rather than by a fixed amount.
                    await asyncio.sleep(self.delay)
                writer.write(data)
                await writer.drain()
        except (ConnectionError, OSError, asyncio.CancelledError):
            pass
        finally:
            try:
                if writer.can_write_eof():
                    writer.write_eof()
            except (OSError, RuntimeError):
                pass


class DelayLine:
    """A TCP listener that forwards to ``(host, port)`` with ``rtt`` split evenly over the two
    directions - the shape a real path has, where each leg carries half the round trip."""

    def __init__(self, target_host: str, target_port: int, rtt_ms: float) -> None:
        self._target = (target_host, target_port)
        self._one_way = (rtt_ms / 1000.0) / 2.0
        self._server: asyncio.AbstractServer | None = None
        self._tasks: set[asyncio.Task] = set()

    @property
    def port(self) -> int:
        assert self._server is not None and self._server.sockets
        return self._server.sockets[0].getsockname()[1]

    async def start(self) -> DelayLine:
        self._server = await asyncio.start_server(self._handle, "127.0.0.1", 0)
        return self

    async def close(self) -> None:
        for task in list(self._tasks):
            task.cancel()
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        try:
            up_r, up_w = await asyncio.open_connection(*self._target)
        except OSError:
            writer.close()
            return
        pipe = DelayedPipe(self._one_way)
        task = asyncio.gather(
            pipe.pump(reader, up_w), pipe.pump(up_r, writer), return_exceptions=True
        )
        self._tasks.add(task)  # type: ignore[arg-type]
        try:
            await task
        finally:
            self._tasks.discard(task)  # type: ignore[arg-type]
            for w in (writer, up_w):
                try:
                    w.close()
                except OSError:
                    pass


async def measure_rtt(
    reader: asyncio.StreamReader, writer: asyncio.StreamWriter, *, rounds: int = 20
) -> tuple[float, float]:
    """Round trip over an established, paired connection: write one byte, wait for the echo.

    Returns (p50, p99) in milliseconds. The peer must be echoing."""
    samples: list[float] = []
    for _ in range(rounds):
        start = time.perf_counter()
        writer.write(b"\x00")
        await writer.drain()
        await reader.readexactly(1)
        samples.append((time.perf_counter() - start) * 1000.0)
    samples.sort()
    return samples[len(samples) // 2], samples[min(len(samples) - 1, int(len(samples) * 0.99))]
