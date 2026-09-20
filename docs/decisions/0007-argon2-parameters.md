# ADR-0007: argon2id parameters against the VPS ceiling

**Status:** **accepted on 2026-09-05** with the product owner choosing option (b). Implemented and measured.

## Context

Storing passwords with argon2id is required by section 14 of the product document, and has been implemented since
week one. But the week-five load test (`docs/load-test-week5.md`) showed its cost on a single core is
considerable:

- **About 210 ms of CPU per sign-in** with the current parameters.
- The server runs with one worker deliberately (plan decision, section 2), so this cost competes with the event
  loop itself.
- It was softened by moving the derivation off the event loop and capping concurrency at two (a memory ceiling of
  about 128 MB), but the queue remains: **20 concurrent sign-ins take a full session from 78 ms to about 10.7
  seconds** at 200 connections.

## What actually reduces the risk

A full sign-in is rare in normal operation: the access token lives 15 minutes and the refresh token 30 days, so
restarting the server does **not** cause a sign-in storm (clients reconnect with a token they already have). The
only real storm is the first run of a new fleet, or the aftermath of rotating `JWT_SECRET`.

## Options

| Option | Effect | Cost |
|---|---|---|
| **a. Leave it as it is** | The strongest resistance to guessing, unchanged | A sign-in storm slows sessions by seconds |
| **b. OWASP's recommended parameters** (m=19 MiB, t=2, p=1) | About a fifth of the cost, still within published guidance | Theoretically less resistance to a hardware attack |
| **c. Keep it, and rate-limit sign-in per account** | Bounds the storm without weakening the derivation | Does not help the first run of a fleet |

## Decision

**(b)**: `m=19 MiB, t=2, p=1`. OWASP's parameters are sufficient for storing passwords behind rate limiting and
account lockout, both of which this server implements; and the operational ceiling of a single-core VPS is a real
constraint rather than a theoretical one.

## The measured effect

Measured against the real target: the production image in a container limited to `--cpus 1 --memory 2g`, twenty
concurrent sign-ins, separate users per parameter set so that rehashing does not contaminate the measurement, and
two rounds per case (the second being the steady state).

| Parameters | Total time | p50 | p99 |
|---|---|---|---|
| Before: m=64 MiB, t=3, p=4 | 4770 and 4778 ms | about 2710 ms | about 4700 ms |
| **After: m=19 MiB, t=2, p=1** | **367 and 361 ms** | **about 241 ms** | **about 346 ms** |

**About thirteen times better** on the batch's total time. A single derivation on the development machine went
from 25 ms to 13, and in the constrained container from about 210 to about 16.

## Compatibility with existing accounts

The parameters live inside the hash itself (`$argon2id$v=19$m=19456,t=2,p=1$…`), so old passwords:
- **stay valid**: verification reads their parameters out of the hash;
- **are upgraded automatically** on the first successful sign-in, through `password_needs_rehash` wired in
  `app/services/auth.py`.

Pinned by two tests in `tests/test_security.py`: one fixes the parameters themselves (so changing them is a
decision rather than a drift), and the other checks that a hash with the old parameters still works and is marked
for upgrade.

## A note

Option (c) — rate-limiting sign-in per account — remains a useful proposal and is not implemented; what exists
today is rate limiting by IP address and account lockout after ten failed attempts.
