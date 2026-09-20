# Week 2 status (2026-09-04)

## What was completed and verified

| Track | Output | Verification |
|---|---|---|
| A: the server | The admin paths complete (`users`, `devices`, `domains`, `sessions`, `security-events`, `diagnostics`, `settings`), `GET /sessions/me`, `POST /diagnostics` to receive the prototype's results, rate limiting on sign-in (5/minute/IP), restricting `/docs` to administrators in production, an internal event bus (`AllowlistPublished`, `SessionEnded`) and `SettingsService` as a seed for week 3 | ruff clean; **127 tests + 1 skipped** on SQLite and **128** on PostgreSQL; a live check confirmed 401 for the anonymous and 200 for the administrator on `/docs`, the refusal of an invalid entry followed by publishing version 1, and a 429 after the fifth attempt |
| B: networking | **ADR-0006 accepted**: `Nerdbank.Streams` behind `IMuxConnection`/`IMuxAcceptor`. `Egress` (the policy, the open handler, the counters, the domain collector, the limits), `Proxy` (CONNECT, the check page, http-to-https redirection, the owning-PID check), `Browser` (locating the path, detecting policies, the Job Object, a polite shutdown), the new Spike commands (`browser`, `--post-to`) | **65** Tunnel tests, **99** Proxy, **50** Egress, **38** Browser, **7** in-process integration; the ADR's five measurements recorded with their numbers |
| C: the application | `ApiClient` complete against the `api.md` contract, `AuthSession` with token rotation and a single concurrent refresh, `AppSettingsStore`, `MockControlChannel` simulating the server per `ws-protocol.md`, `ControlMessageSerializer`, the sign-in screen and the startup flow (a silent restore or the sign-in screen), `SessionCoordinator` to track the session's phases, the `--uninstall-notifications` switch | **153** tests; the whole solution builds **with no errors and no warnings** |
| D: DevOps | Checking the production Compose locally (building the image, TLS through Caddy, restricting `/docs`, signing in, a first backup), correcting the health-check path, the notification-unregistration step in the installer, CI updated with the new projects and the benchmarks separated, the browser launch matrix in the prototype guide | All of the above actually run on this machine |

**The total:** 128 tests on the server and **584** on the client.

## Two real defects found and fixed in the multiplexing layer

1. **A failure disguised as a clean close.** `Fail` was cancelling the cancellation token before setting the
   exception, so the control loop woke up and completed the task successfully before it. The result: the tunnel dying
   at the PONG timeout sometimes appeared as an ordinary close. The operations were reordered so the cause is set
   first.
2. **The transport ending abruptly counted as a clean close.** A network drop or the death of the other end's process
   with no `GOAWAY` completed the task successfully. It now surfaces as an explicit failure.

Why the two matter: in week four the application will decide from this signal which end reason to report to the
server; without the fix it would have recorded "a normal end" in place of "a disconnection".

## What needs human intervention
1. **The code-signing certificate**, if it has not been requested yet (on the critical path for week 6).
2. **A staging VPS** and deploying the server on it.
3. **The technical prototype on Windows** per `docs/spike-runbook.md`: `certtest` on Win10 and Win11, `gather` on two
   or three routers, the ten pairs for the relay gate, and the browser matrix of eight.
4. **QA of the WPF application** on Windows to accept ADR-0001.

## Week 3 (from the plan)
- A: the WebSocket layer (`ConnectionManager`, the heartbeat, `hello`), presence and broadcasting hosts, requests —
  creation, answering, cancelling and expiry.
- B: hardening `Core` and `Tunnel` with additional tests, and supporting whatever the ten pairs' data reveals.
- C: the real `ControlChannel` over WSS with reconnection and a heartbeat, and the main screen with a live host list.
- D: the security test scripts (nmap, CONNECT to private addresses, checking `session_keys`), and the test device
  matrix.
