# The load test — week 5

The goal: to prove success criterion 18 ("the system working on a small, low-cost VPS") with a number rather than an
estimate, and to measure the ceiling of the **single-worker** model the plan intends (section 2), rather than settling
for the risk note "enough for hundreds".

Every number below comes from an actual run: a real uvicorn server, PostgreSQL in Docker, and real WebSocket
connections with a complete `hello` handshake. None of it is from an in-process simulation.

---

## 1. The tool

`backend/loadtest/ws_load.py` — a standalone script outside the `app` package:

```bash
cd backend
LOADTEST_ADMIN_PASSWORD='…' .venv/bin/python -m loadtest.ws_load \
    --users 500 --host-fraction 0.4 \
    --base-url http://127.0.0.1:8000 --admin-email admin@example.com \
    --duration 30 --rate 2 \
    --server-pid 12345 \
    --state /tmp/rb-load.json \
    --json  /tmp/rb-load-500.json
```

- `--server-pid` or `--server-container NAME`: whichever measures the server's memory and CPU.
- `--state`: a file that reuses the accounts and tokens between runs (preparing 500 users costs 500 argon2 operations,
  so there is no point repeating it).
- `--no-announce-listen-port`: announcing `host.available` with no port, so no reachability probe runs.

What it does, in order:

1. Creates `--users` users through `POST /admin/users`, and signs each one in through `POST /auth/login` (which
   registers their device and returns the token).
2. Opens a WebSocket connection per device with a real `hello` handshake, and answers `ping` throughout the run.
3. Makes a `--host-fraction` of them announce `host.available` **one after another**, and after each announcement
   measures how long `hosts.update` takes to reach the last connection — that is, the broadcast's cost **as the number
   of hosts grows**, which is exactly the worry in section 8 of `ws-protocol.md` (the frame is built per recipient).
4. Drives complete cycles of `request.create → request.incoming → accept → session.created → the endpoint exchange →
   connected → active → stats → end → terminate` between random pairs at a rate of `--rate`.
5. Prints: the distribution of setup times, the message latencies (p50/p95/p99), the broadcast's cost, the number of
   errors by code, any connection that dropped, and the server's memory and CPU.

**An operational note:** the server under test must be run with `RATE_LIMIT_ENABLED=false`, because `POST /auth/login`
is limited to 5 requests/minute/IP (`docs/api.md`) and all the fake users come from one address. That same constraint
is a security note recorded in `docs/security-review-server.md`.

Measuring the server: `--server-pid` through `ps`, or `--server-container` through the cgroup counters inside the
container (`memory.current` and `cpu.stat`), which is the method that produced the 1 vCPU numbers below.

---

## 2. The numbers **before** the fix

The development machine (macOS, a multi-core processor; the server, the tool and the database all on the same
machine):

| Connections | Hosts | `hello`→`hosts.snapshot` p50/p99 | `request.create`→`request.incoming` p50 | Full cycle p50 | `hosts.update` to the last recipient | RSS | Cycles succeeded/failed |
|---|---|---|---|---|---|---|---|
| 50 | 20 | 187 / 403 ms | 21 ms | 155 ms | 24 ms | 157 MB | 60 / 0 |
| 200 | 80 | 617 / 1737 ms | 12 ms | 347 ms | 336 ms | 199 MB | 60 / 0 |
| **500** | **200** | **1311 / 6351 ms** | **3768 ms** | **35 370 ms** | **1495 ms** | **779 MB** | **23 / 2** |

At 500 connections real errors appeared for the first time: `not_found` six times and `host_unavailable` once —
sessions that expired at the connect timeout (30 seconds) while their frames were still queued behind the broadcast.

### Where it breaks and why

Three sources of waste, all of them in the `hosts.update` path:

1. **A query per connected user.** `broadcast_hosts_update` ran the availability query once per user (because the list
   excludes their own devices). 500 connections = 500 queries for every single presence change.
2. **JSON serialisation per recipient.** The frame was built and serialised 500 times for a list of 200 items: half a
   million serialisation operations for one field.
3. **Broadcasting for a change that did not happen.** Every `hello` and every disconnection broadcast, even though an
   ordinary client connecting **does not change** the host list at all. Opening N connections cost N broadcasts to N
   recipients — that is, O(N²) — and that is the cause of the 1.3 seconds in the handshake and the 6.3 seconds at p99.

And a fourth cause outside the broadcast: **argon2id on the event loop.** Every sign-in held the whole loop for the
duration of the derivation, so every heartbeat, every broadcast and every session frame on the entire server stopped
for that long.

---

## 3. What was fixed

| # | The fix | File | Effect |
|---|---|---|---|
| 1 | **One** availability query per broadcast, with the exclusion applied in memory | `app/ws/notify.py` | 500 queries → 1 |
| 2 | **One** serialisation reused for every recipient who owns no device in the list; whoever does gets their own copy alone | `app/ws/notify.py` + `Connection.send_text` | O(connections × hosts) → O(hosts) + O(connections) |
| 3 | **Dropping the broadcast if the list has not changed** since the last one | `app/ws/notify.py` | It eliminates the O(N²) connection storm entirely, and eliminates the wasted second broadcast after a reachability probe when the result does not change |
| 4 | argon2 off the event loop (`asyncio.to_thread`) | `app/core/security.py` | Signing in no longer freezes the control channel |
| 5 | A concurrency ceiling for the derivation (`MAX_CONCURRENT_KDF = 2`) | `app/core/security.py` | It bounds argon2's peak memory at ~128 MiB instead of 64 MiB × the pool's thread count |

Fixes 1–3 **do not touch the contract**: the frame sent is byte-for-byte what used to be sent, and section 4 asks for
`hosts.update` "on any change" — and when there is no change there is nothing to send. A new client receives its list
in `hosts.snapshot` as usual.

New tests pin the behaviour in `tests/test_ws_presence.py`: a connection or disconnection that does not change the list
broadcasts nothing, a repeated `host.available` with the same state broadcasts nothing, and every recipient still sees
their own list.

---

## 4. The numbers **after** the fix

### 4.1 The development machine (the same conditions as section 2)

| Connections | Hosts | Handshake p50/p99 | `request.create`→`incoming` p50 | Full cycle p50 | The broadcast to the last recipient | RSS | Cycles |
|---|---|---|---|---|---|---|---|
| 50 | 20 | 177 / 234 ms | 22 ms | 64 ms | 4.7 ms | 153 MB | 60 / 0 |
| 200 | 80 | 59 / 212 ms | 22 ms | 102 ms | 28 ms | 173 MB | 60 / 0 |
| 500 | 200 | 55 / 238 ms | 17 ms | **194 ms** | **135 ms** | 200 MB | 60 / 0 |
| 1000 | 400 | 54 / 210 ms | 329 ms | 1518 ms | 506 ms | 300 MB | 55 / 0 |

At 500: the handshake is **24 times** faster at p50 and **27 times** at p99, the full cycle is **182 times** faster,
the broadcast is **11 times** faster, and there is not one error.

### 4.2 On 1 vCPU / 2 GB in earnest

The same production image (`backend/Dockerfile`) inside `docker run --cpus 1 --memory 2g`, which is as close as can be
measured here to the target VPS:

| Connections | Hosts | Handshake p50/p99 | `request.create`→`incoming` p50 | Full cycle p50 | The broadcast to the last recipient | CPU per broadcast | RSS (KB/connection) | Cycles | Dropped connections |
|---|---|---|---|---|---|---|---|---|---|
| 50 | 20 | 164 / 233 ms | 13.5 ms | 37 ms | 4.9 ms | 10 ms | 78 MB (109) | 60 / 0 | 0 |
| 200 | 80 | 65 / 205 ms | 13.9 ms | 77 ms | 27 ms | 22 ms | 91 MB (60) | 60 / 0 | 0 |
| 500 | 200 | 54 / 231 ms | 12.6 ms | 172 ms | 135 ms | 84 ms | 121 MB (49) | 60 / 0 | 0 |

The instantaneous memory peak was 225 MB, all of it from argon2 while preparing the users, not from the connections.

**A methodological warning:** the database here is in another container with its own CPU budget, while a real VPS
shares one core between FastAPI, PostgreSQL and Caddy. The numbers above are optimistic by that much.

---

## 5. The real ceiling

### 5.1 What actually limits it

Not the number of connections. An idle connection costs **~49 KB** of the server's memory and almost nothing in CPU (a
beat every 20 seconds). 500 connections = 121 MB, that is 6% of 2 GB.

What limits it is **the rate of presence change**, because `hosts.update`'s cost grows in the product of (connections ×
hosts):

| Connections × hosts | CPU per broadcast (1 vCPU) |
|---|---|
| 50 × 20 = 1 000 | 10 ms |
| 200 × 80 = 16 000 | 22 ms |
| 500 × 200 = 100 000 | 84 ms |

That is **≈ 0.85 microseconds per (connection × host)** after the fix. If we allocate 10% of the core to broadcasting:

`the maximum presence changes per second ≈ 0.1 / (0.85e-6 × connections × hosts)`

- 200 connections / 80 hosts → **~7 changes/second**
- 500 connections / 200 hosts → **~1.2 changes/second**
- 1000 connections / 400 hosts → **~0.3 changes/second** (this is where the decline starts: the full cycle takes 1.5
  seconds)

What generates a change? A host enabling "available" or disabling it, a session starting (the host leaves the list),
its ending, and the reachability probe's result. For a team of 500 users with tens of sessions an hour, that is below
0.05 changes/second — under 5% of the ceiling. **The margin is large, but it narrows quadratically.**

### 5.2 The real breaking point: signing in

> **Settled after this section was written:** the OWASP parameters (m=19 MiB, t=2, p=1) were adopted in
> [ADR-0007](decisions/0007-argon2-parameters.md) on 2026-09-05, so the batch of twenty sign-ins went from 4770 to 367
> milliseconds on the same container. The numbers below describe the state **before** that.

argon2id with the library's recommended parameters (t=3, m=64 MiB, p=4) costs **~210 ms of CPU time per sign-in** on
one core. An explicit A/B test was run on the constrained container with 200 live connections:

| | Full cycle p50 | Cycle p95 | CPU p95 |
|---|---|---|---|
| With no sign-ins | **78 ms** | 83 ms | 37% |
| With 20 concurrent sign-ins | **10 752 ms** | 16 586 ms | 103% |

Sign-in throughput under pressure: **4.7/second** with a p50 latency of 4.6 seconds. No connection dropped and no error
appeared — the system slows down and does not break.

Moving argon2 to a separate thread stopped the loop **freezing**, but it does not create a second core: on 1 vCPU the
derivation competes with the loop for the same processor. The two existing protections are the limit of 5
requests/minute/IP and the `MAX_CONCURRENT_KDF` concurrency ceiling, and they do not suffice against twenty different
addresses.

**A realistic mitigation:** the server going down does not cause a sign-in storm, because the client reconnects with
`hello` using an access token that lives 15 minutes, and only signs in if it has expired. Any restart that completes
within 15 minutes passes with no argon2 at all.

### 5.3 The recommendation for success criterion 18

> **A VPS with one core and 2 GB carries 500 concurrent control channels comfortably**, with 121 MB of memory and a
> median session setup of 172 ms, with no dropped connection and not one error.

In detail:

| Range | Verdict |
|---|---|
| Up to 200 connections | Very comfortable: a full cycle of 77 ms, a broadcast of 27 ms, and seven presence changes a second |
| 200–500 connections | **The recommended production range on 1 vCPU** |
| 500–1000 | It works and slows down: the cycle reaches 1.5 seconds at 1000, and one presence change costs a third of a second |
| Above 1000 | Not advised on one core before the Redis Pub/Sub upgrade documented in the plan |

Two conditions attach to that number:
1. **PostgreSQL shares the same core in production**, so subtract a margin. If usage passes 300 active connections the
   cheapest upgrade is a second core, not a second worker (the registry is in memory, and the plan knows that).
2. **Watch the rate of presence change**, not the number of connections. That is the variable that moves quadratically.

---

## 6. Notes on the contract (not implemented — they need review by tracks A and C)

1. **`hosts.update` carries the whole list.** That is the source of the quadratic growth: one host changing sends every
   host to every client. A delta frame (`hosts.added` / `hosts.removed` with one device) makes the cost
   O(connections) instead of O(connections × hosts), and raises the ceiling more than tenfold. This is **a contract
   change** and requires amending sections 4 and 8 of `ws-protocol.md` and the client with it.
2. **There is no message rate limit in the contract.** A per-connection frame budget answering with `rate_limited` was
   added (a code that already exists in section 2's vocabulary), but the contract does not say when it is used. It
   should be documented in section 2 at the first review.
3. **`POST /auth/login` is limited by IP only.** A whole team behind one NAT shares 5 attempts a minute, and twenty
   different addresses get past the limit entirely. The detail is in `docs/security-review-server.md`.

---

## 7. What was not measured

- **No measurement over a real network:** everything is on `localhost`, so there is no propagation delay, no packet
  loss, and no TLS from Caddy. The numbers are the server's cost, not the end user's latency.
- **No measurement under Caddy:** terminating TLS and proxying add a cost that did not enter these numbers.
- **No long-lived measurement:** the longest run was minutes, so nothing here says anything about a memory leak over
  days. It is measured on the VPS after deployment.
- **No measurement of PostgreSQL under pressure**: the database was never the bottleneck in these runs.
