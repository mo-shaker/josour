# Week 6 status (2026-09-05)

Without track D, at the project owner's request. Most of week six's items in the plan had been finished early, so the
tracks were pointed at what actually remained.

## What was completed and verified

| Track | Output | Verification |
|---|---|---|
| A: the server | Closing the two reservations left open by the security review: **a rate-limit policy that matches the threat** ([ADR-0008](decisions/0008-rate-limit-policy.md)), and **automatic detection of suspicious devices** from signals that already existed and were unconsumed; receiving `listener_unauthenticated`; and a full review of the OpenAPI documentation with a test that stops it rotting | ruff clean; **328 tests** on PostgreSQL (was 282) |
| B: networking | **A 32-minute soak test** under load and another at idle, and **fuzzing of the framing layer**, the most dangerous parser in the product and one that had not been exercised | **924** tests across the B layers (was 833) |
| C: the application | The VPN warning, the list at its specific version, sending the security signal, removing the duplication, and **a first-run experience, settings and a support screen** — the application could not be configured by its own user | **350** tests (was 278) |

**The total:** 328 tests on the server and **1274** on the client, with no warnings, and 5 full runs with no failures.

## The rate-limit policy: the number alone was not enough

The limit was 5 requests a minute per IP address, and it fails in both directions: twenty different addresses get past
it, and in return it punishes a whole team behind one NAT.

| Path | Key | Limit |
|---|---|---|
| `POST /auth/login` | IP address | 30/minute |
| `POST /auth/login` | **the submitted email** | 5 per 15 minutes, returned on success |
| `POST /auth/refresh` | IP address | 60/minute |
| `POST /probe` | `user_id` | 10/minute |

The calibration is the deliberate part: the email limit's capacity (5) is **below the account-lockout threshold** (10),
so no burst can lock an account — and the lockout is a denial of service an attacker can trigger. And returning the
credit on a successful sign-in makes failures alone consume the quota, so the limit becomes invisible to a real user.
And the limit on `probe` closes a door that made the server a slow port scanner under its own identity.

## Seven findings from the fuzzing

| # | Severity | Finding |
|---|---|---|
| F-1 | Medium | A malformed control frame from the peer **ended the tunnel as a clean close**, so the server is told of an ordinary end. **This is the third time this class has appeared**, after weeks 2 and 5 |
| F-2 | Medium | The channel-acceptance path had no ceiling of its own, so a hostile host can force unbounded channel creation on the user |
| F-3 | Low | `Completion` failures became unobserved exceptions: a process crash on any host that triggers it |
| F-4 | Low-medium | The relay token's bytes were decoded leniently and so rewritten silently, with the rejection branch dead |
| F-5 | Medium | A diagnostic row per inbound connection during the connect window: memory growth driven by a stranger, and it exceeds the 64 KB limit **so the flood erases its own evidence** |
| F-6 | Low | Every PING held a cancellation registration for the tunnel's whole lifetime |
| F-7 | Medium | **Every healthy session recorded a false security signal**: the losing connection in the symmetric race is closed without a reply per the contract, and it was counted as an unauthenticated attempt |

F-7 is the most serious in practice: a security signal fired by every ordinary session is a signal with no value.

## The soak test: no leak

32 minutes under load (2355 streams, 16.9 gigabytes) and 32 minutes at idle. The heap, open files, threads and
outstanding PINGs: **nothing grows steadily**. And latency is constant at twice the round-trip time.

## The duplication was removed

The firewall check was implemented twice, in `Josour.Tunnel` and `Josour.Infrastructure`, with two different `netsh`
invocations and two different readings of its text. Each track rightly stopped at its own boundary and did not modify
the other's files, so I completed the merge myself: one implementation in `Josour.Core`, called by both sides.

## Contract points settled
- **`GET /healthz`** now carries a product marker and a version. The first-run and settings screens decide from it "is
  this a Josour server", and the check rested on `{"status":"ok"}` alone, which any intermediary imitates, so a wrong
  address came back later in the shape of "wrong password".
- **`vpn_holds_default_route`**: the old key says a VPN adapter exists, and the new one says it actually holds the
  egress route — and that alone predicts a surprising exit address.
- **`unauthenticated_peers_distinct`** is reserved: it distinguishes a scan from dozens of sources from repeated
  attempts from one.
- **The meaning of a zero counter**: the server writes an event only for a positive value, and the decision to send is
  the sender's.

## What needs human intervention
Unchanged: the code-signing certificate, a staging server (the workflow lacks four secrets), running the `session`
tool on two Windows machines (the relay gate hangs on it), and QA.

## Week 7 (from the plan)
Integration fixes and timeout tuning, running E2E across the device matrix, and performance on a real network.
