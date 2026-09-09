"""WebSocket load harness for the Josour control plane.

Standalone (not part of the ``app`` package): it talks to a *running* server over HTTP and
WebSocket exactly like a real client would, so what it measures is the deployed single-uvicorn-
worker model of docs/Josour-MVP-Implementation-Plan.md section 10, not an in-process mock.

    cd backend
    .venv/bin/python -m loadtest.ws_load --users 200 --host-fraction 0.4 \
        --base-url http://127.0.0.1:8000 --admin-email admin@example.com \
        --admin-password ... --server-pid 12345 --duration 60 --rate 2 \
        --state /tmp/rb-load.json --json /tmp/rb-load-200.json

What it does, in order:

1. provisions ``--users`` users through ``POST /admin/users`` and logs each one in through
   ``POST /auth/login`` (which registers its device and hands back the access token);
2. opens one WebSocket per device with a real ``hello`` handshake, answering ``ping`` forever;
3. has ``--host-fraction`` of them announce ``host.available``, one at a time, timing the
   ``hosts.update`` fan-out after each announcement so the cost is measured *as the host count
   grows* (docs/ws-protocol.md section 8: the frame is rendered per recipient);
4. drives full request -> accept -> session.created -> endpoint exchange -> connected -> active
   -> end cycles between random guest/host pairs at ``--rate`` cycles per second;
5. reports setup-time, latency and fan-out distributions, error counts by code, dropped
   connections, and the server process's RSS and CPU.

The server under test should run with ``RATE_LIMIT_ENABLED=false``: ``POST /auth/login`` is
capped per client IP (ADR-0008) and every simulated user logs in from this one address. The
per-email half of that limit would not fire here - each simulated user has its own email and the
logins succeed, which refunds the token - but the per-IP cap would.
"""

import argparse
import asyncio
import json
import os
import random
import resource
import statistics
import subprocess
import sys
import time
import uuid
from collections import Counter
from dataclasses import dataclass, field
from typing import Any

import httpx
import websockets
from websockets.asyncio.client import ClientConnection, connect

PASSWORD = "loadtest-password-1234"
TOKEN_REUSE_MARGIN_SECONDS = 300.0
"""Cached access tokens are reused only while at least this much of their life is left."""


# ---------------------------------------------------------------- statistics helpers


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round(fraction * (len(ordered) - 1))))
    return ordered[index]


def summarise(values: list[float]) -> dict[str, float]:
    if not values:
        return {"n": 0}
    return {
        "n": len(values),
        "min": round(min(values), 2),
        "p50": round(percentile(values, 0.50), 2),
        "p95": round(percentile(values, 0.95), 2),
        "p99": round(percentile(values, 0.99), 2),
        "max": round(max(values), 2),
        "mean": round(statistics.fmean(values), 2),
    }


def fmt(name: str, values: list[float], unit: str = "ms") -> str:
    s = summarise(values)
    if not s.get("n"):
        return f"  {name:<34} (no samples)"
    return (
        f"  {name:<34} n={s['n']:<5} p50={s['p50']:>8.2f} p95={s['p95']:>8.2f} "
        f"p99={s['p99']:>8.2f} max={s['max']:>8.2f} {unit}"
    )


# ---------------------------------------------------------------- server process sampling


def _run(argv: list[str]) -> str:
    try:
        return subprocess.run(  # noqa: S603 - fixed argv built from typed options
            argv, capture_output=True, text=True, timeout=15
        ).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return ""


class ProcessSampler:
    """RSS and CPU of the server, either as a host process (``--server-pid``, via ``ps``) or as
    a container (``--server-container``, via the cgroup counters ``docker exec`` can read).

    The container form is what backs the 1 vCPU / 2 GB numbers: ``docker run --cpus 1
    --memory 2g`` is the closest thing to the target VPS that can be measured here."""

    def __init__(
        self, pid: int | None, container: str | None = None, interval: float = 2.0
    ) -> None:
        self.pid = pid
        self.container = container
        self.interval = interval
        self.rss_mb: list[float] = []
        self.cpu_percent: list[float] = []
        self._task: asyncio.Task[None] | None = None

    @property
    def enabled(self) -> bool:
        return self.pid is not None or self.container is not None

    def read(self) -> tuple[float, float] | None:
        """``(rss_mb, cumulative_cpu_seconds)`` or ``None`` when the server cannot be seen."""
        if self.container is not None:
            return self._read_container()
        if self.pid is None:
            return None
        out = _run(["ps", "-o", "rss=,time=", "-p", str(self.pid)])
        if not out:
            return None
        rss_kb, clock = out.split()[:2]
        return float(rss_kb) / 1024.0, _cpu_seconds(clock)

    def _read_container(self) -> tuple[float, float] | None:
        out = _run(
            [
                "docker",
                "exec",
                str(self.container),
                "sh",
                "-c",
                "cat /sys/fs/cgroup/memory.current; grep usage_usec /sys/fs/cgroup/cpu.stat",
            ]
        )
        lines = out.split()
        if len(lines) < 3:
            return None
        return float(lines[0]) / (1024 * 1024), float(lines[2]) / 1_000_000.0

    async def read_async(self) -> tuple[float, float] | None:
        """:meth:`read` without blocking the harness's event loop."""
        return await asyncio.to_thread(self.read)

    async def start(self) -> None:
        if not self.enabled:
            return

        async def run() -> None:
            previous = await self.read_async()
            previous_at = time.perf_counter()
            while True:
                await asyncio.sleep(self.interval)
                current = await self.read_async()
                now = time.perf_counter()
                if current is None:
                    return
                self.rss_mb.append(current[0])
                if previous is not None and now > previous_at:
                    self.cpu_percent.append(
                        100.0 * (current[1] - previous[1]) / (now - previous_at)
                    )
                previous, previous_at = current, now

        self._task = asyncio.create_task(run(), name="sampler")

    async def stop(self) -> None:
        if self._task is not None:
            self._task.cancel()
            await asyncio.gather(self._task, return_exceptions=True)


def _cpu_seconds(clock: str) -> float:
    """``ps -o time=`` renders ``[[dd-]hh:]mm:ss`` (macOS adds hundredths)."""
    days = 0.0
    if "-" in clock:
        head, clock = clock.split("-", 1)
        days = float(head)
    parts = [float(p) for p in clock.split(":")]
    while len(parts) < 3:
        parts.insert(0, 0.0)
    return days * 86400 + parts[0] * 3600 + parts[1] * 60 + parts[2]


# ---------------------------------------------------------------- provisioning


@dataclass
class Account:
    email: str
    user_id: str
    device_id: str
    device_secret: str
    access_token: str = ""
    token_expires_at: float = 0.0

    def to_json(self) -> dict[str, Any]:
        return {
            "email": self.email,
            "user_id": self.user_id,
            "device_id": self.device_id,
            "device_secret": self.device_secret,
            "access_token": self.access_token,
            "token_expires_at": self.token_expires_at,
        }

    @property
    def token_is_fresh(self) -> bool:
        return bool(self.access_token) and (
            self.token_expires_at - time.time() > TOKEN_REUSE_MARGIN_SECONDS
        )


class Provisioner:
    """Creates (or reuses) the accounts and devices the connections need."""

    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.state_path = args.state
        self.accounts: dict[str, Account] = {}
        self.login_ms: list[float] = []
        self.created = 0
        self._load_state()

    def _load_state(self) -> None:
        if not self.state_path or not os.path.exists(self.state_path):
            return
        with open(self.state_path, encoding="utf-8") as handle:
            for row in json.load(handle).get("accounts", []):
                self.accounts[row["email"]] = Account(**row)

    def _save_state(self) -> None:
        if not self.state_path:
            return
        payload = {"accounts": [a.to_json() for a in self.accounts.values()]}
        with open(self.state_path, "w", encoding="utf-8") as handle:
            json.dump(payload, handle)

    def email(self, index: int) -> str:
        return f"lt-{self.args.prefix}-{index:05d}@loadtest.local"

    async def run(self, client: httpx.AsyncClient) -> list[Account]:
        admin_token = await self._admin_token(client)
        wanted = [self.email(i) for i in range(self.args.users)]
        missing = [e for e in wanted if e not in self.accounts]
        if missing:
            await self._create_users(client, admin_token, missing)
        await self._login_all(client, wanted)
        self._save_state()
        return [self.accounts[e] for e in wanted]

    async def _admin_token(self, client: httpx.AsyncClient) -> str:
        response = await client.post(
            "/api/v1/auth/login",
            json={
                "email": self.args.admin_email,
                "password": self.args.admin_password,
                "device": {
                    "id": None,
                    "secret": None,
                    "name": "loadtest-admin",
                    "os_version": "loadtest",
                    "os_build": None,
                },
            },
        )
        response.raise_for_status()
        return str(response.json()["access_token"])

    async def _create_users(
        self, client: httpx.AsyncClient, admin_token: str, emails: list[str]
    ) -> None:
        headers = {"Authorization": f"Bearer {admin_token}"}
        semaphore = asyncio.Semaphore(self.args.provision_concurrency)

        async def create(email: str) -> None:
            async with semaphore:
                response = await client.post(
                    "/api/v1/admin/users",
                    headers=headers,
                    json={
                        "email": email,
                        "password": PASSWORD,
                        "display_name": email.split("@", 1)[0],
                        "role": "user",
                    },
                )
                if response.status_code == 409:  # left over from an earlier run
                    return
                response.raise_for_status()
                self.created += 1

        print(f"provisioning {len(emails)} users ...", flush=True)
        await asyncio.gather(*(create(email) for email in emails))

    async def _login_all(self, client: httpx.AsyncClient, emails: list[str]) -> None:
        semaphore = asyncio.Semaphore(self.args.provision_concurrency)

        async def login(email: str) -> None:
            existing = self.accounts.get(email)
            if existing is not None and existing.token_is_fresh:
                return
            device = (
                {"id": existing.device_id, "secret": existing.device_secret}
                if existing
                else {"id": None, "secret": None}
            )
            payload = {
                "email": email,
                "password": PASSWORD,
                "device": {
                    **device,
                    "name": f"LT-{email.split('@', 1)[0]}",
                    "os_version": "Windows 11 Pro",
                    "os_build": "22631",
                },
            }
            async with semaphore:
                started = time.perf_counter()
                response = await client.post("/api/v1/auth/login", json=payload)
                self.login_ms.append((time.perf_counter() - started) * 1000)
            response.raise_for_status()
            body = response.json()
            kept = existing.device_secret if existing else ""
            self.accounts[email] = Account(
                email=email,
                user_id=body["user"]["id"],
                device_id=body["device"]["id"],
                device_secret=body["device"]["secret"] or kept,
                access_token=body["access_token"],
                token_expires_at=time.time() + float(body["expires_in"]),
            )

        print(f"logging in {len(emails)} devices ...", flush=True)
        await asyncio.gather(*(login(email) for email in emails))


# ---------------------------------------------------------------- one simulated client


@dataclass
class Metrics:
    ws_connect_ms: list[float] = field(default_factory=list)
    handshake_ms: list[float] = field(default_factory=list)
    request_incoming_ms: list[float] = field(default_factory=list)
    peer_endpoint_ms: list[float] = field(default_factory=list)
    accept_to_created_ms: list[float] = field(default_factory=list)
    active_ms: list[float] = field(default_factory=list)
    cycle_ms: list[float] = field(default_factory=list)
    fanout: list[dict[str, float]] = field(default_factory=list)
    errors: Counter[str] = field(default_factory=Counter)
    cycles_ok: int = 0
    cycles_failed: int = 0
    dropped: list[dict[str, Any]] = field(default_factory=list)


class LoadClient:
    """One control channel: a reader task that answers ``ping`` and routes frames by type."""

    def __init__(self, account: Account, url: str, metrics: Metrics) -> None:
        self.account = account
        self.url = url
        self.metrics = metrics
        self.ws: ClientConnection | None = None
        self.queues: dict[str, asyncio.Queue[tuple[float, dict[str, Any]]]] = {}
        self.hosts_updates = 0
        self.last_hosts_update: float = 0.0
        self.hosts_update_event = asyncio.Event()
        self.is_host = False
        self.busy = False
        self.closed_reason: str | None = None
        self._reader: asyncio.Task[None] | None = None

    # -- plumbing -------------------------------------------------------------------------

    def queue(self, message_type: str) -> asyncio.Queue[tuple[float, dict[str, Any]]]:
        if message_type not in self.queues:
            self.queues[message_type] = asyncio.Queue()
        return self.queues[message_type]

    async def send(self, payload: dict[str, Any]) -> None:
        assert self.ws is not None
        await self.ws.send(json.dumps(payload))

    async def expect(self, message_type: str, timeout: float = 10.0) -> dict[str, Any]:
        _, frame = await asyncio.wait_for(self.queue(message_type).get(), timeout)
        return frame

    async def expect_at(self, message_type: str, timeout: float = 10.0) -> tuple[float, dict]:
        return await asyncio.wait_for(self.queue(message_type).get(), timeout)

    # -- lifecycle ------------------------------------------------------------------------

    async def open(self) -> None:
        started = time.perf_counter()
        self.ws = await connect(self.url, max_queue=256, ping_interval=None, open_timeout=30)
        connected_at = time.perf_counter()
        self.metrics.ws_connect_ms.append((connected_at - started) * 1000)
        self._reader = asyncio.create_task(self._read_loop(), name=f"rd-{self.account.email}")
        await self.send(
            {
                "type": "hello",
                "token": self.account.access_token,
                "device_id": self.account.device_id,
                "app_version": "loadtest/1.0",
            }
        )
        await self.expect("hello.ack", timeout=30)
        await self.expect("hosts.snapshot", timeout=30)
        self.metrics.handshake_ms.append((time.perf_counter() - started) * 1000)

    async def _read_loop(self) -> None:
        assert self.ws is not None
        try:
            async for raw in self.ws:
                now = time.perf_counter()
                try:
                    frame = json.loads(raw)
                except ValueError:
                    continue
                kind = frame.get("type")
                if kind == "ping":
                    await self.send({"type": "pong"})
                    continue
                if kind == "pong":
                    continue
                if kind == "hosts.update":
                    self.hosts_updates += 1
                    self.last_hosts_update = now
                    self.hosts_update_event.set()
                    continue
                if kind == "error":
                    self.metrics.errors[str(frame.get("code", "?"))] += 1
                self.queue(str(kind)).put_nowait((now, frame))
        except websockets.exceptions.ConnectionClosed as exc:
            self.closed_reason = f"closed:{exc.code}"
            self.metrics.dropped.append({"email": self.account.email, "code": exc.code})
        except Exception as exc:  # noqa: BLE001 - the harness must survive any client fault
            self.closed_reason = f"error:{type(exc).__name__}"
            self.metrics.dropped.append({"email": self.account.email, "error": str(exc)})

    async def close(self) -> None:
        if self.ws is not None:
            await self.ws.close()
        if self._reader is not None:
            self._reader.cancel()
            await asyncio.gather(self._reader, return_exceptions=True)


# ---------------------------------------------------------------- the run


CANDIDATES = [
    {"type": "lan", "ip": "192.168.1.50", "port": 51820},
    {"type": "public", "ip": "203.0.113.77", "port": 51820},
]
CERT_FP = "a" * 64


class LoadRun:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.metrics = Metrics()
        self.sampler = ProcessSampler(args.server_pid, args.server_container)
        self.clients: list[LoadClient] = []
        self.hosts: list[LoadClient] = []
        self.guests: list[LoadClient] = []
        self.random = random.Random(args.seed)
        self.rss_before_connect: tuple[float, float] | None = None
        self.rss_after_connect: tuple[float, float] | None = None

    @property
    def ws_url(self) -> str:
        base = self.args.base_url.rstrip("/")
        return base.replace("https://", "wss://").replace("http://", "ws://") + "/ws"

    # -- phases ---------------------------------------------------------------------------

    async def connect_all(self, accounts: list[Account]) -> None:
        print(f"opening {len(accounts)} WebSocket connections ...", flush=True)
        semaphore = asyncio.Semaphore(self.args.connect_concurrency)

        async def one(account: Account) -> LoadClient | None:
            client = LoadClient(account, self.ws_url, self.metrics)
            async with semaphore:
                try:
                    await client.open()
                except Exception as exc:  # noqa: BLE001
                    self.metrics.dropped.append({"email": account.email, "setup": str(exc)})
                    return None
            return client

        results = await asyncio.gather(*(one(a) for a in accounts))
        self.clients = [c for c in results if c is not None]
        host_count = int(len(self.clients) * self.args.host_fraction)
        shuffled = list(self.clients)
        self.random.shuffle(shuffled)
        self.hosts, self.guests = shuffled[:host_count], shuffled[host_count:]

    async def announce_hosts(self) -> None:
        """Announce one host at a time, timing the fan-out after each announcement.

        docs/ws-protocol.md section 8 renders ``hosts.update`` per recipient, so this is the
        measurement that shows how the broadcast scales with the number of hosts."""
        print(f"announcing {len(self.hosts)} hosts (timed fan-out) ...", flush=True)
        for index, host in enumerate(self.hosts, start=1):
            live = [c for c in self.clients if c.closed_reason is None]
            for client in live:
                client.hosts_update_event.clear()
            before = await self.sampler.read_async()
            started = time.perf_counter()
            payload: dict[str, Any] = {"type": "host.available", "available": True}
            if self.args.announce_listen_port:
                payload["listen_port"] = 51820
            await host.send(payload)
            host.is_host = True
            await self._await_fanout(live, started)
            elapsed_ms = (max(c.last_hosts_update for c in live) - started) * 1000
            after = await self.sampler.read_async()
            self.metrics.fanout.append(
                {
                    "hosts": index,
                    "connections": len(live),
                    "ms_to_last": round(elapsed_ms, 2),
                    "cpu_ms": round(((after[1] - before[1]) * 1000) if before and after else 0, 1),
                }
            )

    async def _await_fanout(self, live: list[LoadClient], started: float) -> None:
        deadline = started + self.args.fanout_timeout
        pending = [c for c in live if not c.hosts_update_event.is_set()]
        while pending and time.perf_counter() < deadline:
            await asyncio.wait(
                [asyncio.create_task(c.hosts_update_event.wait()) for c in pending[:64]],
                timeout=0.05,
                return_when=asyncio.ALL_COMPLETED,
            )
            pending = [c for c in live if not c.hosts_update_event.is_set()]

    async def measure_double_broadcast(self) -> dict[str, Any]:
        """A host toggling availability *with* ``listen_port`` costs two broadcasts today: the
        presence change, then the reachability probe's own ``hosts.update``. This counts them."""
        if not self.hosts:
            return {}
        sample = self.hosts[0]
        watcher = self.clients[-1]
        result: dict[str, Any] = {}
        for label, listen_port in (("without_listen_port", None), ("with_listen_port", 51820)):
            await sample.send({"type": "host.available", "available": False})
            await self._quiet(watcher)
            before = watcher.hosts_updates
            payload: dict[str, Any] = {"type": "host.available", "available": True}
            if listen_port is not None:
                payload["listen_port"] = listen_port
            await sample.send(payload)
            await asyncio.sleep(self.args.probe_settle)
            result[label] = watcher.hosts_updates - before
        return result

    async def _quiet(self, watcher: LoadClient, idle: float = 1.5, limit: float = 60.0) -> None:
        """Wait until ``watcher`` has seen no ``hosts.update`` for ``idle`` seconds."""
        deadline = time.perf_counter() + limit
        while time.perf_counter() < deadline:
            seen = watcher.hosts_updates
            await asyncio.sleep(idle)
            if watcher.hosts_updates == seen:
                return

    async def drive_sessions(self) -> None:
        """Full cycles at ``--rate`` per second for ``--duration`` seconds."""
        if not self.hosts or not self.guests:
            return
        print(f"driving sessions for {self.args.duration}s at {self.args.rate}/s ...", flush=True)
        deadline = time.perf_counter() + self.args.duration
        interval = 1.0 / self.args.rate if self.args.rate > 0 else 0.0
        running: set[asyncio.Task[None]] = set()
        while time.perf_counter() < deadline:
            pair = self._pick_pair()
            if pair is not None:
                task = asyncio.create_task(self._cycle(*pair))
                running.add(task)
                task.add_done_callback(running.discard)
            await asyncio.sleep(interval)
        if running:
            await asyncio.wait(running, timeout=30)

    def _pick_pair(self) -> tuple[LoadClient, LoadClient] | None:
        free_hosts = [h for h in self.hosts if not h.busy and h.closed_reason is None]
        free_guests = [g for g in self.guests if not g.busy and g.closed_reason is None]
        if not free_hosts or not free_guests:
            return None
        return self.random.choice(free_guests), self.random.choice(free_hosts)

    async def _cycle(self, guest: LoadClient, host: LoadClient) -> None:
        guest.busy = host.busy = True
        started = time.perf_counter()
        try:
            ref = uuid.uuid4().hex[:8]
            sent_at = time.perf_counter()
            await guest.send(
                {
                    "type": "request.create",
                    "ref": ref,
                    "host_device_id": host.account.device_id,
                    "duration_min": self.args.session_minutes,
                }
            )
            await guest.expect("request.created", timeout=self.args.op_timeout)
            incoming_at, incoming = await host.expect_at("request.incoming", self.args.op_timeout)
            self.metrics.request_incoming_ms.append((incoming_at - sent_at) * 1000)

            accepted_at = time.perf_counter()
            await host.send(
                {
                    "type": "request.accept",
                    "ref": uuid.uuid4().hex[:8],
                    "request_id": incoming["request_id"],
                }
            )
            await guest.expect("request.result", timeout=self.args.op_timeout)
            created_at, created = await guest.expect_at("session.created", self.args.op_timeout)
            await host.expect("session.created", timeout=self.args.op_timeout)
            self.metrics.accept_to_created_ms.append((created_at - accepted_at) * 1000)
            session_id = created["session_id"]

            endpoint_at = time.perf_counter()
            await guest.send(
                {
                    "type": "session.endpoint",
                    "session_id": session_id,
                    "cert_fp_sha256": CERT_FP,
                    "candidates": CANDIDATES,
                }
            )
            peer_at, _ = await host.expect_at("session.peer_endpoint", self.args.op_timeout)
            self.metrics.peer_endpoint_ms.append((peer_at - endpoint_at) * 1000)
            await host.send(
                {
                    "type": "session.endpoint",
                    "session_id": session_id,
                    "cert_fp_sha256": CERT_FP,
                    "candidates": CANDIDATES,
                }
            )
            await guest.expect("session.peer_endpoint", timeout=self.args.op_timeout)

            connected_at = time.perf_counter()
            await host.send(
                {
                    "type": "session.connected",
                    "session_id": session_id,
                    "winner_type": "lan",
                    "connect_ms": 45,
                    "tls_version": "1.2",
                }
            )
            active_at, _ = await guest.expect_at("session.active", self.args.op_timeout)
            await host.expect("session.active", timeout=self.args.op_timeout)
            self.metrics.active_ms.append((active_at - connected_at) * 1000)

            await host.send(
                {
                    "type": "session.stats",
                    "session_id": session_id,
                    "bytes_up": 1024,
                    "bytes_down": 4096,
                }
            )
            await guest.send(
                {
                    "type": "session.end",
                    "session_id": session_id,
                    "reason": "guest_ended",
                    "bytes_up": 2048,
                    "bytes_down": 8192,
                    "domains": [],
                }
            )
            await guest.expect("session.terminate", timeout=self.args.op_timeout)
            await host.expect("session.terminate", timeout=self.args.op_timeout)
            self.metrics.cycles_ok += 1
            self.metrics.cycle_ms.append((time.perf_counter() - started) * 1000)
        except (TimeoutError, AssertionError, KeyError) as exc:
            self.metrics.cycles_failed += 1
            self.metrics.errors[f"cycle:{type(exc).__name__}"] += 1
        finally:
            # Drain anything the cycle left behind so the next one starts clean.
            for client in (guest, host):
                for queue in client.queues.values():
                    while not queue.empty():
                        queue.get_nowait()
            guest.busy = host.busy = False

    # -- driver ---------------------------------------------------------------------------

    async def run(self) -> dict[str, Any]:
        limits = resource.getrlimit(resource.RLIMIT_NOFILE)
        resource.setrlimit(resource.RLIMIT_NOFILE, (min(limits[1], 65536), limits[1]))
        await self.sampler.start()
        wall_started = time.perf_counter()
        cpu_started = await self.sampler.read_async()
        async with httpx.AsyncClient(
            base_url=self.args.base_url, timeout=httpx.Timeout(120.0)
        ) as http:
            provisioner = Provisioner(self.args)
            accounts = await provisioner.run(http)
        # Sampled with nothing in flight, so the delta is what the connections themselves cost.
        self.rss_before_connect = await self.sampler.read_async()
        await self.connect_all(accounts)
        self.rss_after_connect = await self.sampler.read_async()
        await self.announce_hosts()
        double = await self.measure_double_broadcast()
        steady_rss = await self.sampler.read_async()
        await self.drive_sessions()
        cpu_ended = await self.sampler.read_async()
        await self.sampler.stop()
        report = self._report(provisioner, double, steady_rss, cpu_started, cpu_ended, wall_started)
        await asyncio.gather(*(c.close() for c in self.clients), return_exceptions=True)
        return report

    def _report(
        self,
        provisioner: Provisioner,
        double: dict[str, Any],
        steady: tuple[float, float] | None,
        cpu_started: tuple[float, float] | None,
        cpu_ended: tuple[float, float] | None,
        wall_started: float,
    ) -> dict[str, Any]:
        m = self.metrics
        fanout_last = m.fanout[-5:]
        return {
            "config": {
                "users": self.args.users,
                "host_fraction": self.args.host_fraction,
                "hosts": len(self.hosts),
                "guests": len(self.guests),
                "rate_per_second": self.args.rate,
                "duration_s": self.args.duration,
                "base_url": self.args.base_url,
            },
            "wall_seconds": round(time.perf_counter() - wall_started, 1),
            "connections_opened": len(self.clients),
            "connections_dropped": len(m.dropped),
            "dropped_detail": m.dropped[:10],
            "login_ms": summarise(provisioner.login_ms),
            "users_created": provisioner.created,
            "ws_connect_ms": summarise(m.ws_connect_ms),
            "handshake_ms": summarise(m.handshake_ms),
            "request_incoming_ms": summarise(m.request_incoming_ms),
            "accept_to_session_created_ms": summarise(m.accept_to_created_ms),
            "peer_endpoint_ms": summarise(m.peer_endpoint_ms),
            "session_active_ms": summarise(m.active_ms),
            "full_cycle_ms": summarise(m.cycle_ms),
            "fanout_by_host_count": m.fanout,
            "fanout_tail": fanout_last,
            "hosts_update_frames_per_announcement": double,
            "cycles_ok": m.cycles_ok,
            "cycles_failed": m.cycles_failed,
            "errors_by_code": dict(m.errors),
            "server": {
                "rss_mb_before_connect": round(self.rss_before_connect[0], 1)
                if self.rss_before_connect
                else None,
                "rss_mb_after_connect": round(self.rss_after_connect[0], 1)
                if self.rss_after_connect
                else None,
                "kb_per_connection": round(
                    (self.rss_after_connect[0] - self.rss_before_connect[0])
                    * 1024
                    / max(1, len(self.clients)),
                    1,
                )
                if self.rss_before_connect and self.rss_after_connect
                else None,
                "rss_mb_steady": round(steady[0], 1) if steady else None,
                "rss_mb_max": round(max(self.sampler.rss_mb), 1) if self.sampler.rss_mb else None,
                "cpu_percent_p95": round(percentile(self.sampler.cpu_percent, 0.95), 1)
                if self.sampler.cpu_percent
                else None,
                "cpu_percent_max": round(max(self.sampler.cpu_percent), 1)
                if self.sampler.cpu_percent
                else None,
                "cpu_seconds_total": round(cpu_ended[1] - cpu_started[1], 2)
                if cpu_started and cpu_ended
                else None,
            },
        }


def print_report(report: dict[str, Any]) -> None:
    config = report["config"]
    print("\n" + "=" * 86)
    print(
        f"users={config['users']}  hosts={config['hosts']}  guests={config['guests']}  "
        f"rate={config['rate_per_second']}/s  duration={config['duration_s']}s"
    )
    print("=" * 86)
    print(f"  connections opened                 {report['connections_opened']}")
    print(f"  connections dropped                {report['connections_dropped']}")
    for name, key in (
        ("login (POST /auth/login)", "login_ms"),
        ("ws connect (TCP+upgrade)", "ws_connect_ms"),
        ("handshake (hello -> snapshot)", "handshake_ms"),
        ("request.create -> request.incoming", "request_incoming_ms"),
        ("accept -> session.created", "accept_to_session_created_ms"),
        ("endpoint -> peer_endpoint", "peer_endpoint_ms"),
        ("connected -> session.active", "session_active_ms"),
        ("full cycle", "full_cycle_ms"),
    ):
        stats = report[key]
        if stats.get("n"):
            print(
                f"  {name:<34} n={stats['n']:<5} p50={stats['p50']:>8.2f} "
                f"p95={stats['p95']:>8.2f} p99={stats['p99']:>8.2f} max={stats['max']:>8.2f} ms"
            )
    print("\n  hosts.update fan-out (time until the last connection has the new list)")
    for row in report["fanout_by_host_count"]:
        if row["hosts"] % max(1, len(report["fanout_by_host_count"]) // 10) == 0:
            print(
                f"    hosts={row['hosts']:<5} connections={row['connections']:<5} "
                f"ms_to_last={row['ms_to_last']:>9.2f}  server_cpu_ms={row['cpu_ms']:>8.1f}"
            )
    frames = report["hosts_update_frames_per_announcement"]
    print(f"\n  hosts.update frames per announcement {frames}")
    print(f"  cycles ok / failed                 {report['cycles_ok']} / {report['cycles_failed']}")
    print(f"  errors by code                     {report['errors_by_code'] or '{}'}")
    server = report["server"]
    print(
        f"  server rss before / after connect  {server['rss_mb_before_connect']} / "
        f"{server['rss_mb_after_connect']} MB  ({server['kb_per_connection']} KB/connection)"
    )
    print(
        f"  server rss steady / max            {server['rss_mb_steady']} / "
        f"{server['rss_mb_max']} MB"
    )
    print(
        f"  server cpu p95 / max / total       {server['cpu_percent_p95']}% / "
        f"{server['cpu_percent_max']}% / {server['cpu_seconds_total']}s"
    )
    print(f"  wall time                          {report['wall_seconds']}s")
    print("=" * 86 + "\n")


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--users", type=int, default=50, help="concurrent connections to open")
    parser.add_argument("--host-fraction", type=float, default=0.4)
    parser.add_argument("--base-url", default="http://127.0.0.1:8000")
    parser.add_argument("--admin-email", default="admin@example.com")
    parser.add_argument("--admin-password", default=os.environ.get("LOADTEST_ADMIN_PASSWORD", ""))
    parser.add_argument("--prefix", default="a", help="namespace for the generated accounts")
    parser.add_argument("--state", default="", help="JSON file caching accounts between runs")
    parser.add_argument("--json", dest="json_out", default="", help="write the report here")
    parser.add_argument("--server-pid", type=int, default=None)
    parser.add_argument(
        "--server-container",
        default=None,
        help="measure this docker container instead of a host pid (cgroup counters)",
    )
    parser.add_argument("--duration", type=float, default=30.0, help="seconds of session load")
    parser.add_argument("--rate", type=float, default=1.0, help="session cycles started/second")
    parser.add_argument("--session-minutes", type=int, default=5)
    parser.add_argument("--provision-concurrency", type=int, default=8)
    parser.add_argument("--connect-concurrency", type=int, default=32)
    parser.add_argument("--op-timeout", type=float, default=20.0)
    parser.add_argument("--fanout-timeout", type=float, default=30.0)
    parser.add_argument("--probe-settle", type=float, default=4.0)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument(
        "--no-announce-listen-port",
        dest="announce_listen_port",
        action="store_false",
        help="announce host.available without listen_port (skips the reachability probe)",
    )
    parser.set_defaults(announce_listen_port=True)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if not args.admin_password:
        print("--admin-password (or LOADTEST_ADMIN_PASSWORD) is required", file=sys.stderr)
        return 2
    report = asyncio.run(LoadRun(args).run())
    print_report(report)
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as handle:
            json.dump(report, handle, indent=2)
        print(f"report written to {args.json_out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
