# Week 5 status (2026-09-05)

Without track D, at the project owner's request.

## What was completed and verified

| Track | Output | Verification |
|---|---|---|
| A: the server | **A real load test** with the `backend/loadtest/ws_load.py` tool exposed a collapse at 500 connections (a full session taking 35 seconds) and fixed its cause in the `hosts.update` broadcast; **a security review item by item** against sections 14 and 15 of the product document; and the `list-sessions` and `end-session` CLI commands | ruff clean; **279 tests + 1 skipped** on SQLite and **280** on PostgreSQL (was 263) |
| B: networking | **A performance measurement under a real international RTT** with a link simulator (delay, bandwidth and jitter) instead of loopback; a VPN warning with a confidence-rated result; and respecting a corporate proxy on the direct path | **773** tests across the B layers (was 698) |
| C: the application | **Full Arabic localisation** with two resource bundles, 148 keys and correct right-to-left direction; the host's disclosure before acceptance showing the actual list of sites for the request's version; and the edge cases (sleep and wake, a network change, token expiry, an abandoned browser, clock drift) | **278** tests (was 207) |
| Integration | Adjusting the adaptive window after the measurement, and correcting the source it is measured from in the contract | **1111** tests on the client, 6 full runs with no failures |

**The total:** 280 tests on the server and **1111** on the client, the build has no warnings, and the whole solution
runs in 35 seconds.

## The VPS ceiling: success criterion 18 now has evidence

It had no support until now. After fixing the broadcast, on a container with one core and 2 GB of memory:

| Connections | Hosts | Handshake p50/p99 | Full cycle p50 | Broadcast cost | Memory |
|---|---|---|---|---|---|
| 50 | 20 | 164/233 ms | 37 ms | 4.9 ms | 78 MB |
| 200 | 80 | 65/205 ms | 77 ms | 27 ms | 91 MB |
| 500 | 200 | 54/231 ms | 172 ms | 135 ms | 121 MB |

**The recommendation: 500 concurrent control channels on a single-core VPS**, with 200 to 500 as the production range.
The governing variable is not the number of connections but **the rate of presence change**; realistic traffic for a
fleet of 500 users is below 0.05 changes a second against a capacity of 1.2.

**The real fragility was in signing in, not in WebSocket:** deriving the password cost about 210 milliseconds of CPU.
It was raised in [ADR-0007](decisions/0007-argon2-parameters.md) as a security-posture decision that is not taken
implicitly inside a performance review, **and the product owner adopted the OWASP parameters on 2026-09-05 and they
were implemented**: twenty concurrent sign-ins on the constrained container went from 4770 to 367 milliseconds, about
thirteen times better. Existing accounts work and are upgraded automatically on first sign-in.

## A privacy leak closed

`ENV=dev` raised root logging to DEBUG, which enables logging of SQLAlchemy's bound parameters: **the names of the
sites browsed, presence addresses, and every row touched**. A direct violation of section 15. The database loggers
were pinned above DEBUG whatever the application's level, and a test was added that drives a full session and then
searches every log with the security script's own patterns.

The full review is in [docs/security-review-server.md](security-review-server.md): 34 requirements, 22 met, 4 fixed, 8
not the server's concern, **and no requirement unmet**, with 4 met under a recorded reservation.

## Performance: a real shortcoming exposed and fixed

Measuring over loopback flattered a windowed protocol. Under an international RTT:

| RTT | Before (a 1 MiB window) | After (a banded window) |
|---|---|---|
| 50 ms | 162 Mbit/s | 162 Mbit/s |
| 150 ms | **56** Mbit/s | **111** Mbit/s |
| 300 ms | **28** Mbit/s | **113** Mbit/s |

Page loading was not affected to begin with (a 0% to 2% increase over a direct connection), nor was video. What is
affected is **downloading a single work file**, a use the product document treats as expected (section 13.1). The
window is now derived from the round-trip time, and the concurrent stream limit matches it, so the memory budget stays
256 megabytes per side in every band.

**A correction in the contract:** my first wording said "derived from `connect_ms`", which is wrong: `connect_ms`
measures the whole connection (TCP, TLS, authentication and the candidate race) and so is three to four times the real
RTT, which raises the session a band or two and lowers the stream limit needlessly. The source is now an explicit
round trip on the authenticated stream before the mux is created.

## An old race closed

`Fail` published the close flag **before** setting the failure's cause, so if two paths woke together `Finish`
preceded the exception and **a dead tunnel appeared as a clean close**: no death event, and the application reports a
wrong end reason to the server. This is of the same class as the two defects fixed in week two, and this path of it
had remained.

## What needs human intervention
1. **The code-signing certificate** (the critical path for week 6).
2. **A staging VPS**: the workflow is ready and lacks four secrets.
3. **The technical prototype on Windows** with the `session` command: it alone proves traffic leaves from the other
   machine's address, and the relay gate still hangs on it.
4. **QA** to accept ADR-0001, now including the Arabic interface.

*(ADR-0007 was accepted and implemented on 2026-09-05.)*

## Week 6 (from the plan)
Packaging and deployment: the signed installer and the SmartScreen check, preparing production, and a joint security
review. And the relay service if its gate opens.
