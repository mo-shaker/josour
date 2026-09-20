# Week 4 status (2026-09-05)

## What was completed and verified

| Track | Output | Verification |
|---|---|---|
| A: the server | The session cycle complete: `session.endpoint` with an order-independent exchange, `session.connected` (the host only) and `session.active`, `session.connect_failed` with diagnostics recorded, monotonic `session.stats`, `session.end` with a role check; the connect-timeout and `expires_at` timers on the same scheduler, cancelled inside the single termination path; an `end_reason` summary in the administrator's report | ruff clean; **262 tests + 1 skipped** on SQLite and **263** on PostgreSQL (was 197) |
| C: the application | `SessionCoordinator` became the real coordinator: building the tunnel from `session.created`, exchanging endpoints, connecting, launching the work browser and waiting for the check page, a monotonic counter, statistics every 30 seconds, and a single idempotent termination however many things trigger it; a complete session panel in the interface | **207** tests (was 183) |
| B: the tool and hardening | A `session` command with no interface, driving the real stack on two machines, with a JSON event log and exit codes; and hardening of the security parsers | **698** tests across the B layers (was 488) |
| D: DevOps | Deploying staging automatically from CI with a backup before deployment and an automatic rollback on failure; **a full restore exercise on a clean environment**; documenting the vulnerabilities as regression items in the acceptance list | Every CI file is valid; the restore actually succeeded |

**The total:** 263 tests on the server and **905** on the client, and the build has no warnings.

## Three real vulnerabilities closed

Found while hardening the parsers, and all of them exploitable:

1. **Header injection (HTTP request smuggling):** header values and the request line were not checked for control
   characters, so an `LF` inside a header injected a second header or request into the origin connection on the direct
   http path.
2. **SSRF from the user's side:** address literals were refused, but **a hostname that resolves to** loopback or a
   private address was connected to, making the local proxy a bridge to whatever the user's machine is listening on.
   The check now happens after resolution and before any socket.
3. **Name normalisation failing open:** `IdnMapping` without STD3 passed NUL, `:`, `/` and spaces through to the DNS
   query and the `Host` header. Validation is now strict, and the deprecated IPv4 wrapping `::a.b.c.d` was closed too.

Documented as regression items in `docs/acceptance-checklist.md`.

## Two contract conflicts found and settled

Track A and track C were building the two ends of the same message with two different understandings, which only
surfaces in integration:

- **The client claimed `expired`** when the duration ran out, and the server refused it with `bad_request` because it
  is one of the server's own judgements. The resolution: the client tears down locally at once with no `session.end`
  and waits for `session.terminate`. **The live session confirmed it:** it ended as `Expired` through the server, and
  the client never sent `session.end` once.
- **The tunnel's death** suggested `guest_disconnected`/`host_disconnected` and the server refused both, even though
  the client is the only source of that news while both control channels stay alive. The resolution: the server
  accepts them under an inverted rule (each side names whoever disappeared), and still refuses its own three
  judgements.

The reference table of end reasons is now in section 9 of `docs/ws-protocol.md`.

## The duplicated coordinator was settled

Both tracks wrote a session coordinator — that is, two copies of the security cleanup order. They were unified on
`SessionCoordinator`, the other was deleted, and the tool was rewired to it. The unification uncovered a third
vulnerability: the `expired` reason could leak in from the peer's farewell message, so a central guard was added.

## Live verification (independent)

A full session between two processes against a real server: sign-in → control channel → request and acceptance →
endpoint exchange → connection over `lan` in 45 ms with TLS 1.2 → `curl` through the tunnel returned **200 and the
public IP address in the body** → statistics every 30 seconds → the duration expiring. The server's log:
`ended / expired / lan / 1.2 / connect_result=ok`, and `session_keys` empty.

## Test stability
Three new intermittent tests were treated the same way. **8 full runs, 7240 results, no failures.**

## What needs human intervention
Unchanged since week one and not started yet. It is now more urgent, because week four delivered a tool that makes
testing on Windows possible without waiting for the interface:
1. **The code-signing certificate** (on the critical path for week 6).
2. **A staging VPS** and deploying the server on it (the workflow is ready and lacks four secrets).
3. **The technical prototype on Windows** per `docs/spike-runbook.md`, including the `session` command between two
   real machines: it alone proves traffic leaves from the other machine's address.
4. **QA of the WPF application** to accept ADR-0001.

## Week 5 (from the plan)
- A: hardening and cleanup, and relay support if the gate opens.
- B: whatever the ten pairs' data reveals.
- C: polishing the interface and the remaining edge cases.
- D: running the runbook on a clean VPS, and checking SmartScreen on the signed installer.
