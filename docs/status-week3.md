# Week 3 status (2026-09-04)

## What was completed and verified

| Track | Output | Verification |
|---|---|---|
| A: the server | The WebSocket layer complete on `/ws`: `ConnectionManager` with one connection per device, the `hello` timeout, the heartbeat and the close codes, `hello.ack` and `hosts.snapshot`; the presence service with `hosts.update` broadcasts and background reachability probing; the full request cycle (creation, cancellation, acceptance, rejection, expiry by timer) through to `session.created` for both parties with a 32-byte secret; wiring the event bus to broadcasting `allowlist.updated` and `session.terminate`; clearing presence and ending pending sessions at startup | ruff clean; **196 tests + 1 skipped** on SQLite and **197** on PostgreSQL (was 128); a live check with a real `websockets` client covered the whole flow and codes 4409 and 1012 |
| B: networking | **`TunnelSession` finally implements `ITunnelSession`** for both roles: preparing the certificate, the listener and the candidates, the symmetric connection, wiring the egress layer on the host and the proxy on the user, watching for death with a suggested reason, and a cleanup order matching the contract and callable repeatedly; `RelayTransport` and its preamble protocol, so the relay decision becomes a setting change rather than a redesign | **106** Tunnel tests, **55** Egress, **104** Proxy, **9** integration; the four benchmarks still pass |
| C: the application | The real `ControlChannel` over WSS: a single receive loop, a heartbeat, binding `ref` to replies and errors, reconnection with exponential backoff distinguishing the terminal codes (4401 refreshes the token once, 4403 signs out, 4409 stops), and re-announcing "available" after coming back; a live host list and the request flow from both sides | **183** tests (was 153), among them an in-process WebSocket server |
| D: DevOps | Four security scripts in `scripts/security/` actually exercised (session keys, the listener, blocking internal addresses, log cleanliness), the device and network matrix in `docs/test-matrix.md`, and pinning the contract decisions in `ws-protocol.md` and `api.md` | Every script was tried on positive and negative cases |

**The total:** 197 tests on the server and **671** on the client, and the solution builds with no warnings.

## Test stability

Six intermittent tests appeared under full parallelism (they would have broken CI up to 60% of the time). All of them
are in the tests rather than in production code, and each was treated by its cause:

- **Contention for the CPU:** parallelism was disabled inside the assemblies that bind real sockets (Proxy,
  Infrastructure, Tunnel, Egress and E2E) through `xunit.runner.json`.
- **Asserting on an instantaneous phase** while the simulated channel advances on its own: the assertion moved to the
  ordered log.
- **A list read while being modified** from the dispatch loop: it became a snapshot under a lock.
- **A connection reset** arrives as a `SocketException` rather than an `IOException`, and may strike at the connect
  itself: the assertion moved to the counters, tolerating both paths.
- **Values the other end observes** after our read completes: they are now awaited until they settle.
- **Detecting the tunnel's death** relied on a default liveness timeout of 60 seconds exceeding the test's own
  timeout: a short liveness was configured for the tests with a wider margin.

**Verification:** 10 consecutive full runs, 6710 results, with no failures.

## What needs human intervention
Unchanged from the last two weeks and not started yet:
1. **The code-signing certificate** (on the critical path for week 6).
2. **A staging VPS** and deploying the server on it.
3. **The technical prototype on Windows**: `certtest` on Win10 and Win11, `gather` on two or three routers, the ten
   pairs for the relay gate, and the browser matrix of eight.
4. **QA of the WPF application** to accept ADR-0001.

## Week 4 (from the plan)
- A: the rest of the session cycle on the server: `session.endpoint`/`peer_endpoint`/`connected`/`connect_failed`/
  `stats`/`end`, the 30-second connect timeout, the `expires_at` timer, deleting `session_keys`, and recording
  `connect_diagnostics`.
- B: supporting whatever the ten pairs' data reveals, and further hardening.
- C: wiring `TunnelSession` and the browser into the full session cycle: launching the work browser, detecting
  `browser_not_proxied` through the check page, reporting statistics, and the shutdown order.
- D: updating staging automatically from CI, with backup and restore exercised on a clean server.
