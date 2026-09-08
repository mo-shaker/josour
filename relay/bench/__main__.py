"""Relay benchmarks (ADR-0009 requires a measurement before the transport is relied on).

Four questions, in the order they matter:

1. **What does the hop cost?** A relay is store-and-forward, so it adds latency by construction.
   The geographic part of that is arithmetic (Egypt -> Jeddah -> Saudi), not something a laptop
   can measure. What a laptop *can* measure is the part the software adds on top of the wire,
   which is the part a code change could ever make worse.
2. **Is the Python pump the ceiling?** This was the recorded reservation in ADR-0009. A relay that
   caps a session below the tunnel's own throughput would make the transport decision costly.
3. **What happens with several sessions at once?** Five users is the release target.
4. **What does an idle paired session hold?** Memory per session sets the real capacity limit.

The relay runs as a **separate process**, as it does in production, so its CPU and memory are its
own and not mixed with the load generator's.

Run:  python -m bench            (all)
      python -m bench latency    (one group)
"""

from __future__ import annotations

import asyncio
import contextlib
import os
import platform
import statistics
import subprocess
import sys
import textwrap
import time
import uuid

import psutil

from bench.link import DelayLine, measure_rtt
from relay.protocol import REPLY_LENGTH, Role, Status, build_preamble
from relay.tokens import mint

SECRET = "bench-secret-that-is-at-least-32-bytes-long"
HOST = "127.0.0.1"
CHUNK = 256 * 1024
METRICS_CPU = False
METRICS_RSS = False


def probe_process_metrics() -> tuple[bool, bool]:
    """Are per-process CPU and RSS trustworthy here? Ask, do not assume.

    This environment (ARM64 Windows) returns 0.0 CPU for a process that has just burned four
    seconds, and a constant 6.1 MB RSS for one holding 300 MB. Printing those as measurements
    would be worse than printing nothing, and silently omitting them would hide that ADR-0009's
    reservation about the asyncio pump's CPU cost is still open. So the harness checks, says what
    it found, and the columns appear on a host where they mean something.
    """
    burn = textwrap.dedent(
        """
        import time
        x = 0
        blob = bytearray(120 * 1024 * 1024)
        blob[::4096] = b"\x01" * len(blob[::4096])
        end = time.time() + 2.5
        while time.time() < end:
            x += 1
        """
    )
    proc = subprocess.Popen([sys.executable, "-c", burn])
    try:
        ps = psutil.Process(proc.pid)
        time.sleep(1.5)
        cpu = ps.cpu_times()
        rss_mb = ps.memory_info().rss / (1024 * 1024)
        return (cpu.user + cpu.system) > 0.05, rss_mb > 60
    except psutil.Error:
        return False, False
    finally:
        proc.terminate()
        with contextlib.suppress(Exception):
            proc.wait(timeout=5)


def _free_port() -> int:
    """A port the OS says is free right now. Hard-coded ports collide with a leftover relay from
    an interrupted run, and the failure mode of that collision is measuring the wrong process."""
    import socket

    with socket.socket() as s:
        s.bind((HOST, 0))
        return int(s.getsockname()[1])


def _high_resolution_timer() -> None:
    """Windows quantises timer waits to ~15.6 ms by default, which is larger than everything this
    file tries to measure: at a nominal 40 ms round trip the delay line produced 50 ms, and the
    relay figure was four quantised sleeps rather than four forwarding costs. timeBeginPeriod(1)
    brings the granularity to 1 ms for the life of the process. Section 2.1 of the doc reports the
    calibration that proves the line is honest afterwards."""
    if sys.platform != "win32":
        return
    import ctypes

    ctypes.windll.winmm.timeBeginPeriod(1)  # type: ignore[attr-defined]


# ---------------------------------------------------------------- harness


class RelayProcess:
    """The relay as the operator runs it: its own process, its own event loop."""

    def __init__(self, port: int | None = None) -> None:
        self.port = port or _free_port()
        self._proc: subprocess.Popen | None = None
        self.ps: psutil.Process | None = None

    async def __aenter__(self) -> RelayProcess:
        env = os.environ | {
            "RELAY_SECRET": SECRET,
            "RELAY_HOST": HOST,
            "RELAY_PORT": str(self.port),
            "LOG_LEVEL": "WARNING",
            "PAIR_TIMEOUT_SECONDS": "60",
            "IDLE_TIMEOUT_SECONDS": "600",
            "MAX_SESSIONS": "256",
            "MAX_CONNECTIONS_PER_IP": "512",
        }
        self._log = open(f"bench-relay-{self.port}.log", "w", encoding="utf-8")
        self._proc = subprocess.Popen(
            [sys.executable, "-m", "relay"], env=env, stdout=self._log, stderr=self._log
        )
        self.ps = psutil.Process(self._proc.pid)
        for _ in range(100):
            # Liveness first. A fixed port that another process already holds made the relay exit
            # on bind while the poll below happily connected to the *stale* one - so a whole run
            # measured a process this harness had not started. Ports are ephemeral now, and a dead
            # child fails the run instead of being silently substituted.
            if not self.alive():
                raise RuntimeError(f"relay exited at start-up; see {self._log.name}")
            try:
                reader, writer = await asyncio.open_connection(HOST, self.port)
                writer.close()
                with contextlib.suppress(Exception):
                    await writer.wait_closed()
                break
            except OSError:
                await asyncio.sleep(0.1)
        else:  # pragma: no cover - the relay failed to come up
            raise RuntimeError("relay did not start")
        self.ps.cpu_percent(None)  # prime the CPU sampler
        return self

    def check_alive(self, where: str) -> None:
        """Called around every measurement: a number from a relay that died is not a number."""
        if not self.alive():
            raise RuntimeError(f"relay died during {where}; see {self._log.name}")

    def alive(self) -> bool:
        return self._proc is not None and self._proc.poll() is None

    async def __aexit__(self, *_: object) -> None:
        if self._proc is not None:
            self._proc.terminate()
            with contextlib.suppress(Exception):
                self._proc.wait(timeout=10)
        with contextlib.suppress(Exception):
            self._log.close()

    def rss_mb(self) -> float:
        assert self.ps is not None
        return self.ps.memory_info().rss / (1024 * 1024)

    def cpu_seconds(self) -> float | None:
        """Per-process CPU, or None where the platform will not report it.

        On this ARM64 Windows box both psutil and the raw Win32 GetProcessTimes return 0.0 even
        for a process that has just burned four seconds of pure CPU, so a CPU column here would be
        a fabricated zero. Reporting None keeps the benchmark honest and leaves ADR-0009's
        reservation about the asyncio pump's CPU cost open until it runs on the real host."""
        assert self.ps is not None
        cpu = self.ps.cpu_times()
        total = cpu.user + cpu.system
        return total if total > 0 else None


def _rss(relay: RelayProcess) -> str:
    """The memory column, or nothing at all where the platform does not report it."""
    return f" rss_mb={relay.rss_mb():.1f}" if METRICS_RSS else ""


async def pair(port: int, session: uuid.UUID) -> tuple[tuple, tuple]:
    """Both parties of one session, connected and paired. Returns (guest, host) stream pairs."""

    async def dial(role: Role):
        reader, writer = await asyncio.open_connection(HOST, port)
        writer.write(build_preamble(session, role, mint(SECRET, session, role)))
        await writer.drain()
        return reader, writer

    guest, host = await asyncio.gather(dial(Role.GUEST), dial(Role.HOST))
    for reader, _ in (guest, host):
        reply = await asyncio.wait_for(reader.readexactly(REPLY_LENGTH), 30)
        if Status(reply[1]) is not Status.PAIRED:
            raise RuntimeError(f"relay refused: {Status(reply[1]).wire_name}")
    return guest, host


async def _pair_via(guest_port: int, host_port: int, session: uuid.UUID) -> tuple[tuple, tuple]:
    """Like :func:`pair`, but each party reaches the relay through its own leg."""

    async def dial(port: int, role: Role):
        reader, writer = await asyncio.open_connection(HOST, port)
        writer.write(build_preamble(session, role, mint(SECRET, session, role)))
        await writer.drain()
        return reader, writer

    guest, host = await asyncio.gather(dial(guest_port, Role.GUEST), dial(host_port, Role.HOST))
    for reader, _ in (guest, host):
        reply = await asyncio.wait_for(reader.readexactly(REPLY_LENGTH), 30)
        if Status(reply[1]) is not Status.PAIRED:
            raise RuntimeError(f"relay refused: {Status(reply[1]).wire_name}")
    return guest, host


async def close_all(*pairs) -> None:
    for _, writer in pairs:
        with contextlib.suppress(Exception):
            writer.close()
            await writer.wait_closed()


# ---------------------------------------------------------------- 1. latency


async def bench_latency() -> None:
    """The relay's own cost, isolated from geography.

    The comparison is only fair if both paths carry the **same total wire delay**, so for a
    nominal round trip R the direct baseline puts one delay line of R between the peers, and the
    relay path puts R/2 on each leg. A well-placed relay is exactly that case: sitting on the
    path, its two legs add up to roughly the direct route (Egypt -> Jeddah -> Saudi). What is left
    after subtracting the wire is what the software costs, and it is the only part a code change
    can move."""
    print("\n== 1. added latency ==")
    async with RelayProcess() as relay:
        port = relay.port
        for nominal in (0.0, 40.0, 120.0):
            direct_p50 = await _direct_rtt(nominal)
            relay_p50, relay_p99 = await _relay_rtt(port, nominal)
            print(
                f"[latency] nominal_rtt={nominal:.0f}ms direct_p50={direct_p50:.2f}ms "
                f"relay_p50={relay_p50:.2f}ms relay_p99={relay_p99:.2f}ms "
                f"added={relay_p50 - direct_p50:.2f}ms"
            )


async def _direct_rtt(nominal_rtt: float) -> float:
    """peer -> (whole RTT) -> peer, no hop."""
    echoed = await asyncio.start_server(_echo, HOST, 0)
    line = await DelayLine(HOST, echoed.sockets[0].getsockname()[1], nominal_rtt).start()
    try:
        reader, writer = await asyncio.open_connection(HOST, line.port)
        p50, _ = await asyncio.wait_for(measure_rtt(reader, writer), 60)
        await close_all((reader, writer))
        return p50
    finally:
        await line.close()
        echoed.close()  # not wait_closed(): in 3.12 it waits on handlers that may still be idle


async def _relay_rtt(port: int, nominal_rtt: float) -> tuple[float, float]:
    """peer -> (half) -> relay -> (half) -> peer: the same wire, with the hop in the middle."""
    guest_line = await DelayLine(HOST, port, nominal_rtt / 2).start()
    host_line = await DelayLine(HOST, port, nominal_rtt / 2).start()
    session = uuid.uuid4()
    guest, host = await _pair_via(guest_line.port, host_line.port, session)
    echo_task = asyncio.create_task(_echo_pair(host))
    try:
        return await asyncio.wait_for(measure_rtt(*guest), 60)
    finally:
        echo_task.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await echo_task
        await close_all(guest, host)
        await guest_line.close()
        await host_line.close()


async def _echo(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    with contextlib.suppress(Exception):
        while True:
            data = await reader.read(65536)
            if not data:
                break
            writer.write(data)
            await writer.drain()


async def _echo_pair(peer: tuple) -> None:
    reader, writer = peer
    await _echo(reader, writer)


# ---------------------------------------------------------------- 2. throughput


async def bench_throughput() -> None:
    """One session, bulk transfer, no artificial delay: this is the pump's own ceiling."""
    print("\n== 2. single-session throughput ==")
    payload = os.urandom(CHUNK)
    async with RelayProcess() as relay:
        port = relay.port
        for megabytes in (64, 256):
            session = uuid.uuid4()
            guest, host = await pair(port, session)
            total = megabytes * 1024 * 1024
            start = time.perf_counter()
            sender = asyncio.create_task(_send(guest[1], payload, total))
            received = await _drain(host[0], total)
            await sender
            elapsed = time.perf_counter() - start
            relay.check_alive("throughput")
            await close_all(guest, host)
            mbps = (received / (1024 * 1024)) / elapsed
            print(
                f"[throughput] size={megabytes}MB time={elapsed * 1000:.0f}ms "
                f"rate={mbps:.1f}MB/s ({mbps * 8:.0f}Mbit/s)" + _rss(relay)
            )


async def _send(writer: asyncio.StreamWriter, payload: bytes, total: int) -> None:
    sent = 0
    while sent < total:
        n = min(len(payload), total - sent)
        writer.write(payload[:n])
        await writer.drain()
        sent += n


async def _drain(reader: asyncio.StreamReader, total: int) -> int:
    got = 0
    while got < total:
        chunk = await reader.read(min(CHUNK, total - got))
        if not chunk:
            break
        got += len(chunk)
    return got


# ---------------------------------------------------------------- 3. concurrency


async def bench_concurrency() -> None:
    """Several sessions at once. Five users is the release target; the higher rows are there to
    show where the curve bends rather than to claim the product needs them."""
    print("\n== 3. concurrent sessions ==")
    payload = os.urandom(CHUNK)
    per_session = 32 * 1024 * 1024
    async with RelayProcess() as relay:
        port = relay.port
        for count in (1, 5, 10, 20):
            pairs = [await pair(port, uuid.uuid4()) for _ in range(count)]
            start = time.perf_counter()

            async def one(guest, host):
                begin = time.perf_counter()
                sender = asyncio.create_task(_send(guest[1], payload, per_session))
                await _drain(host[0], per_session)
                await sender
                return time.perf_counter() - begin

            times = await asyncio.gather(*(one(g, h) for g, h in pairs))
            elapsed = time.perf_counter() - start
            relay.check_alive(f"concurrency({count})")
            rss = _rss(relay)
            for g, h in pairs:
                await close_all(g, h)
            total_mb = count * per_session / (1024 * 1024)
            print(
                f"[concurrent] sessions={count:2d} aggregate={total_mb / elapsed:6.1f}MB/s "
                f"({total_mb / elapsed * 8:5.0f}Mbit/s) "
                f"per_session={total_mb / elapsed / count:5.1f}MB/s "
                f"slowest={max(times) * 1000:.0f}ms fastest={min(times) * 1000:.0f}ms" + rss
            )


# ---------------------------------------------------------------- 4. pairing and footprint


async def bench_pairing() -> None:
    """What the handshake costs, and what an idle paired session holds."""
    print("\n== 4. pairing and footprint ==")
    async with RelayProcess() as relay:
        port = relay.port
        samples = []
        for _ in range(50):
            session = uuid.uuid4()
            start = time.perf_counter()
            guest, host = await pair(port, session)
            samples.append((time.perf_counter() - start) * 1000.0)
            await close_all(guest, host)
        samples.sort()
        print(
            f"[pairing] n=50 p50={statistics.median(samples):.2f}ms "
            f"p99={samples[int(len(samples) * 0.99)]:.2f}ms max={samples[-1]:.2f}ms"
        )

        if not METRICS_RSS:
            print("[footprint] skipped: this host does not report per-process memory")
            return

        await asyncio.sleep(1.0)
        idle_before = relay.rss_mb()
        held = [await pair(port, uuid.uuid4()) for _ in range(50)]
        await asyncio.sleep(1.0)
        idle_after = relay.rss_mb()
        print(
            f"[footprint] idle_rss_mb={idle_before:.1f} with_50_sessions_mb={idle_after:.1f} "
            f"per_session_kb={(idle_after - idle_before) * 1024 / 50:.0f}"
        )
        for g, h in held:
            await close_all(g, h)


# ---------------------------------------------------------------- main


GROUPS = {
    "latency": bench_latency,
    "throughput": bench_throughput,
    "concurrency": bench_concurrency,
    "pairing": bench_pairing,
}


async def main() -> None:
    _high_resolution_timer()
    chosen = sys.argv[1:] or list(GROUPS)
    print(
        f"relay benchmarks | python {platform.python_version()} | "
        f"{platform.system()} {platform.release()} | {platform.machine()} | "
        f"{psutil.cpu_count(logical=True)} logical cores"
    )
    cpu_ok, rss_ok = probe_process_metrics()
    global METRICS_CPU, METRICS_RSS
    METRICS_CPU, METRICS_RSS = cpu_ok, rss_ok
    print(
        f"[env] per_process_cpu={'usable' if cpu_ok else 'UNAVAILABLE'} "
        f"per_process_rss={'usable' if rss_ok else 'UNAVAILABLE'}"
    )
    if not (cpu_ok and rss_ok):
        print(
            "[env] the missing columns are omitted rather than reported as zero; "
            "ADR-0009's CPU reservation stays open until this runs on the deployment host"
        )
    for name in chosen:
        if name not in GROUPS:
            raise SystemExit(f"unknown group {name!r}; pick from {', '.join(GROUPS)}")
        await GROUPS[name]()


if __name__ == "__main__":
    asyncio.run(main())
