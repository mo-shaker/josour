# ADR-0008: the rate-limit policy and the automatic response to abuse

**Status:** **accepted on 2026-09-05** (track A). Implemented and measured. Closes reservations **3.1** (item 14.17)
and **3.2** (item 14.18) in `docs/security-review-server.md`.

The two decisions are in one document because they are two faces of one question: **what does the server do when a
request that should not repeat keeps repeating?** The first part limits the rate before it costs anything, and the
second acts when the repetition goes beyond what a user's mistake can explain.

## Context

Rate limiting today is one line: `POST /auth/login` is limited to **5 requests/minute per IP**, together with a
15-minute account lockout after 10 failed attempts. That limit alone misses the target in three directions:

1. **It does not protect the account.** The key is the source address, so twenty different addresses give 100 attempts
   a minute against **the same account** without any one of them touching its limit. And that is exactly the collapse
   scenario measured in `docs/load-test-week5.md` section 5.2.
2. **It punishes the innocent.** A whole office behind one NAT shares five attempts a minute, so a sixth employee
   opening the application in the morning is refused, and a colleague who mistypes their password three times consumes
   the department's quota.
3. **It leaves two paths exposed.** `POST /auth/refresh` is unlimited and unauthenticated, and `POST /probe` is
   authenticated, unlimited, and makes the server open TCP to any public address the caller names — that is, a slow
   port scanner wearing the server's identity.

And there is an interaction worse than all of the above: **the account lockout is itself a denial of service an
attacker can trigger.** Anyone who knows an employee's email can today send ten wrong attempts in seconds, locking
their account for a quarter of an hour, and repeat it. Any new policy must make **the rate limit what bites first**,
leaving the lockout as a last safety net rather than the first tool.

## What changed in the budget since week five

The policy's economics changed with the adoption of [ADR-0007](0007-argon2-parameters.md): the cost of one derivation
on the constrained container (1 vCPU) dropped from **~210 milliseconds to ~16**, and the batch of twenty sign-ins from
4770 to 367 milliseconds. That is, one core theoretically bears **~62 sign-ins a second** instead of ~4.7. So a tight
per-IP limit is no longer necessary **to protect the CPU**, and it became possible to widen it to serve the office
behind a NAT, provided the account's protection moves to a finer key.

## Decision

Four limits, all in-memory token buckets (a single uvicorn worker, plan decision section 2), all answering with the
existing envelope `429 { "error": { "code": "rate_limited", … } }` and a `Retry-After` header, and all disabled
together with `RATE_LIMIT_ENABLED=false` (what the load tool and the test suite use).

| # | Path | Key | Limit | Setting |
|---|---|---|---|---|
| 1 | `POST /auth/login` | the caller's address | **30 / minute** | `LOGIN_RATE_LIMIT_PER_MINUTE` |
| 2 | `POST /auth/login` | **the submitted email** (SHA-256 hashed) | **5 / 15 minutes**, returned on success | `LOGIN_EMAIL_RATE_LIMIT` + `LOGIN_EMAIL_RATE_LIMIT_WINDOW_MINUTES` |
| 3 | `POST /auth/refresh` | the caller's address | **60 / minute** | `REFRESH_RATE_LIMIT_PER_MINUTE` |
| 4 | `POST /probe` | `user_id` | **10 / minute** | `PROBE_RATE_LIMIT_PER_MINUTE` |

Limits 1 and 2 work together on the same request: the IP limit is checked first (it needs no reading of the body, so
it answers even a malformed one), then the email limit.

### Why 30/minute per IP instead of 5

The per-IP limit is no longer the line of defence for the account — it became a coarse ceiling against flooding from a
single source. At 30 requests/minute a single source costs **0.5 sign-ins/second ≈ 0.8% of a core** with the ADR-0007
parameters. And even in the review's own scenario — **twenty addresses**, each at its ceiling — the cost becomes
10/second, that is **~16% of a core**, against the 103% measured in section 5.2 before ADR-0007. So the server
withstands this attack with no noticeable degradation, while the targeted account is protected by limit 2.

On the other side: an office of thirty employees behind one address all opening the application in the same minute (a
fleet's first run, or after rotating `JWT_SECRET` — the only two cases that generate a real sign-in storm, per
ADR-0007) all get through.

### Why 5 per 15 minutes per email, with the token returned on success

This is the limit that carries the weight, and its numbers were chosen **by comparison with the lockout**, not
arbitrarily:

- **A capacity of 5 is below the lockout threshold of 10.** So a single burst, however fast, cannot lock the account:
  the rate limit always bites first. That is the first thing this decision is for.
- **Reaching ten failed attempts takes at least 15 minutes** (the bucket refills one token every 3 minutes). So
  someone who wants to lock an employee's account deliberately now needs a sustained quarter-hour campaign **for
  every** quarter-hour of lockout, and leaves about ten `login_failed` rows in `security_events` behind them — that
  is, it turned from a silent button press into long and visible activity in `GET /admin/security-events`, and it is
  now material for the automatic detection rule as well.
- **The remaining guessing throughput: one attempt every 3 minutes per account**, combined across every address in the
  world — 480 attempts a day. Against any acceptable password that is nothing, and it is the level at which the OWASP
  recommendation accepts the ADR-0007 parameters (storage behind rate limiting and account lockout).
- **The token is returned on success**: a successful sign-in puts its token back in the bucket, so only **failure**
  consumes the budget. The effect is that the limit never sees the legitimate user at all (a full sign-in is rare to
  begin with: the access token lasts 15 minutes and the refresh token 30 days), and that the bucket's budget now maps
  directly onto the `failed_logins` counter itself, which is what makes the comparison with the threshold of 10 above
  exact rather than approximate.
- **The key is SHA-256 hashed** rather than plain text: a fixed length however long the input (and the key here is
  controlled from outside), and no table of email addresses is left in the process's memory — which is consistent with
  section 15 and note 3.5.

### Why 60/minute per IP on `/auth/refresh`

Deliberately lenient. The path's real cost is SHA-256 and two queries — no derivation — and the purpose is to bound
flooding, not to prevent guessing (the token space is 256 bits, and guessing is out of the question). Legitimate use:
a refresh every 15 minutes per device, so an office of thirty employees with two devices each behind one NAT generates
~4 requests/minute. The limit is fifteen times that, and it remains an effective ceiling against a broken client loop.

### Why 10/minute per user on `/probe`

The path makes the server open a TCP socket to a **public** address the caller names, with a 3-second timeout. Private,
loopback and reserved addresses are refused already, so the danger is not the internal network but **a slow port scan
wearing the server's identity**, appearing in the victim's logs as the server rather than the user.

The key is `user_id`, not the source address: the path is authenticated, and the unit being accounted is the identity,
not the network (and rotating addresses earns nothing). At 10/minute, scanning one host's ports (65535 of them) needs
**~4.5 days per account** — that is, the path stopped being a scanning tool. And legitimate use is far below that:
periodic reachability checking does not go through this path at all (`reachability_probe` is internal), and the use
here is the technical prototype and the client's manual checks, which are units rather than tens.

## Coherence with the derivation concurrency ceiling (`MAX_CONCURRENT_KDF`)

The two mechanisms work on different axes and must be read together:

| | What it limits | Value | What happens when it is exceeded |
|---|---|---|---|
| The rate limits | The **arrival rate** of requests | 30/minute/IP and 5/15 minutes/email | An immediate `429` with `Retry-After` — no derivation at all |
| `MAX_CONCURRENT_KDF` | The **concurrency** of derivation and its peak memory | 2 (≈38 MiB with the ADR-0007 parameters) | The request waits on a semaphore without holding the event loop |

The ordering is deliberate: the rate limit is checked in a FastAPI dependency **before** reaching
`verify_password_async`, so whatever exceeds the ceiling costs no derivation, no semaphore slot and no memory. And the
second ceiling protects against what does get through: even if sixty legitimate requests arrive from sixty addresses in
the same instant, only two run at once, so argon2's peak memory stays ~38 MiB whatever the load, and the batch turns
into a short queue (60 × 16 ms ≈ 0.96 seconds of CPU) rather than into memory pressure.

## The options rejected

| Option | Why it was rejected |
|---|---|
| Keeping 5/minute/IP and adding the email limit on top | Solves the account problem and leaves the NAT problem exactly as it is, even though the derivation's cost after ADR-0007 no longer justifies the tightness |
| A limit on `/auth/login` keyed by (IP + email) together | Looks finer but protects nothing: changing the source address creates a new key, which is precisely what a distributed attacker does |
| Lowering the account-lockout threshold instead of rate limiting | Increases the denial-of-service surface rather than reducing it, and the lockout is documented in `docs/api.md` |
| Storing the limits in Redis | Unjustified in the single-worker model (plan decision section 2); reopened along with the Redis Pub/Sub upgrade itself |
| A limit on `/auth/logout` | Considered and deferred: the path always returns `204` and neither derives nor amplifies; recorded as an open item rather than a decision |

## The effect on the contract

**No change in shapes.** The `rate_limited` envelope, the `Retry-After` header and the 429 code are as they were, and
no field or path was added. What changed is **one documented value**: the line in `docs/api.md` saying "Rate limit on
`/auth/login`: 5 requests/minute/IP" became 30, and with it three new limits needing a line in that same document.
`docs/api.md` is not owned by track A, so the amendment is **requested from its owner** and raised in the week-six
report. Item 11 in section 4 of `docs/security-review-server.md` (verification on the deployed server) was also amended
to match the new numbers.

## The risks accepted

- **The limits are in memory**: restarting the server zeroes every bucket. Acceptable: the window of benefit is
  seconds, and the account lockout and the suspicious-device detection (part two below) work from the database, so they
  survive a restart.
- **The email limit leaves a smaller denial-of-service surface, not none.** Five failed attempts make the targeted
  account receive `429` until a token refills — that is, **three rolling minutes** instead of a hard quarter-hour
  lockout, with continuous rather than all-at-once recovery. That is the cost of any per-account limit, and it was
  accepted knowingly because the alternative (no limit) leaves distributed guessing without a ceiling. Pinned in
  `tests/test_rate_limit.py`: the attack's effect stops at the targeted account alone, and every other account signs in
  from the same addresses.
- **The email key is controlled from outside**: the number of buckets is bounded by `max_keys` (10 thousand) with a
  sweep of full buckets, so it does not grow without limit.
- **Trusting `X-Forwarded-For`** stays as it was: it is read only when uvicorn's `--proxy-headers` middleware trusts it
  (the compose deployment behind Caddy), and otherwise the direct peer's address is used.

---

# Part two: automatic detection of a suspicious device (reservation 3.2 — item 14.18)

## Context

Blocking today is **manual**: an administrator revokes a device, or its owner does. And the signals that indicate a
suspicious device have all been in `security_events` since week one — `login_failed` with its reason, `refresh_reuse`,
`login_locked` — and nobody reads them. Item 14.18 asks for "blocking suspicious devices", and the distance between
"the signal is recorded" and "the device is blocked" is an administrator looking at a panel at the right moment.

But automation here is asymmetrically dangerous: **a wrong revocation cuts a real user off from their device** (it
revokes refresh tokens, clears presence, closes the control channel with code 4403, and they must sign in fully, which
registers a new device with a new id — so they disappear from their colleagues' host lists). A revocation a few minutes
late costs far less than a wrong one. So the governing rule in choosing the thresholds: **no rule may fire on ordinary
use, even at the cost of missing a suspicious case that an administrator catches by hand.**

## Decision

One periodic task, on **the same existing timer schedule** (`app.services.session_timer.scheduler`, key
`device-risk-sweep`) — no second scheduling mechanism in the process. It is armed at the WebSocket layer's startup and
cancelled at its shutdown, and re-arms itself at the end of every pass.

| Setting | Default | Meaning |
|---|---|---|
| `DEVICE_RISK_ENABLED` | `true` | Disables the whole detection |
| `DEVICE_RISK_INTERVAL_SECONDS` | `300` | How many seconds between reads of the events |
| `DEVICE_RISK_WINDOW_MINUTES` | `60` | The look-back window |
| `DEVICE_RISK_SECRET_FAILURES` | `10` | Rule (a)'s threshold; `0` disables it |
| `DEVICE_RISK_REFRESH_REUSE` | `3` | Rule (b)'s threshold; `0` disables it |

### Rule (a): a repeatedly wrong device secret

> **10 `login_failed` events with the reason `device_secret_invalid` for the same `device_id` within 60 minutes, and
> not one `login_success` event for that device inside the window → revoke the device.**

This event occurs at exactly one place in the code (`auth._resolve_device`), and it is **after the password has been
verified**: so its precise meaning is "a party holding the account's password, presenting a registered device id with
the wrong secret" — that is, an attempt to impersonate an existing device, not to guess a password. Ten of those in an
hour is not a user's mistake: a legitimate client that lost its secret (a corrupted DPAPI store, say) registers a new
device with `device.id = null` and does not re-present a wrong secret ten times.

The "no `login_success` inside the window" condition is the safety valve: any successful sign-in from the same device
in the same hour proves the correct secret is still with its owner, so the failures are read as noise (an old
application left open, a restored backup) rather than as a compromise.

### Rule (b): reusing a refresh token more than once

> **3 `refresh_reuse` events for the same `device_id` within 60 minutes → revoke the device.**

Reusing a rotated refresh token is a strong signal that a copy of the token is in a second hand. But **one event is not
enough**, and that is deliberate and grounded in the code's own behaviour: the first reuse revokes the device's whole
chain (`tokens.rotate_refresh_token`), so any token that was in flight at that moment — a client that sent two
concurrent refreshes, or one that dropped after the rotation and retried — arrives afterwards and generates a second,
**entirely legitimate**, event. A threshold of 3 clears that tail: whoever comes back a third time with a token they
have twice been told is dead is not the legitimate client, because the legitimate client receives `401` and signs in
afresh.

### Signals that do not revoke a device — deliberately

| Signal | Why not |
|---|---|
| `listener_unauthenticated` | Describes **the network around the host**, not the device's behaviour: external connections that did not pass `AUTH1` on the listening port. The device here is **the victim**. If we revoked on it, any port scanner on the internet could cut an employee off from their computer by sending them packets. It is swallowed and shown in `GET /admin/security-events` and moves nothing automatically |
| `login_failed` with the reason `bad_password` | Concerns **an account**, not a device, and its handling exists: the email limit in part one, then the account lockout. If we revoked devices over a guessing campaign, we would have handed the attacker a device-level denial of service instead of an account-level one |
| `login_locked` | A result, not a cause; what precedes covers it |
| `login_failed` with the reason `unknown_user` | Has no `device_id` at all |

## The effect and the audit trail

Every automatic revocation goes through the same manual path (`services.devices.revoke_device`) and so inherits its
behaviour entirely: revoking refresh tokens, clearing the presence row, **and a `security_events` row of type
`device_revoked`** carrying the triggering reason explicitly —
`{"by": "auto", "rule": "device_secret_invalid", "matches": 12, "window_minutes": 60}` — then closing the live control
channel with code **4403**. So the event appears in `GET /admin/security-events` like any administrative revocation, and
an administrator can tell the automatic from the manual by the `by` field and read the rule that fired it.

**The operation is idempotent**: `revoke_device` does nothing for an already-revoked device, so a subsequent pass over
the same window writes no second row and sends no second close.

## The risks accepted

- **A gap of up to `DEVICE_RISK_INTERVAL_SECONDS` (5 minutes) between the pattern and the revocation.** Acceptable: the
  harm this mechanism prevents is cumulative rather than instantaneous, and the alternative (evaluating on every event)
  puts security decision logic on the hot path of signing in.
- **The thresholds miss the patient attacker** who stays under 10 failures an hour. Accepted knowingly: this mechanism
  is a safety net rather than a substitute for the administrator's review, and the opposite choice (tight thresholds)
  revokes real devices.
- **The events are read with an upper row limit** on every pass; under a large flood the newest are read first, so it is
  the recent pattern that is evaluated.
