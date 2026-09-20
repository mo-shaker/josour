# The server's security review — against sections 14 and 15 of the product document

An item-by-item review of everything **"14. Security requirements"** and **"15. Privacy requirements"** ask for that
falls on the server. For each item: its state, the evidence in the code, and what was missing.

The states: **met** · **fixed in week 5** · **completed in week 6** · **not the server's concern**.

> **Week 6 update (2026-09-05):** all four reservations were closed. This review no longer contains an unmet item, nor
> an item met under an open reservation. The detail is in section 3.

**The tally:** 34 items —

| State | Count | The items |
|---|---|---|
| Met before week 5 | **21** | 14.2–14.8, 14.11–14.13, 14.16, 14.20, and 15.1–15.5, and 15.10–15.13 |
| Fixed in week 5 | **3** | 14.10 (localhost in the allow list), 14.19 (central termination from the CLI), 14.21 (SQL parameters leaking with `ENV=dev`) |
| **Completed in week 6** | **2** | 14.17 (the four REST limits with the right keys — ADR-0008), 14.18 (automatic suspicious-device detection + consuming `listener_unauthenticated`) |
| Not the server's concern | **8** | 14.1, 14.9, 14.14, 14.15, 15.6, 15.7–15.9 |

**The sum 21 + 3 + 2 + 8 = 34.** Items 14.17 and 14.18 were counted as met before (the first partly, by the WebSocket
frame budget, and the second by manual blocking) and were moved to the week-6 row because what they lacked is exactly
what was closed in it.

**There is no unmet item.** Four items were **met under a reservation**, and all of them have been closed:

| Reservation | Concerns | State |
|---|---|---|
| 3.1 | 14.17 | **Closed (week 6)** — [ADR-0008](decisions/0008-rate-limit-policy.md): a second limit keyed by email on sign-in, a limit on `/auth/refresh`, and a per-user limit on `/probe` |
| 3.2 | 14.18 | **Closed (week 6)** — [ADR-0008](decisions/0008-rate-limit-policy.md) part two: automatic detection with two conservative rules, and `listener_unauthenticated` is now consumed and displayed |
| 3.3 | 14.13 | **Closed (week 5)** — [ADR-0007](decisions/0007-argon2-parameters.md) adopted 2026-09-05, the OWASP parameters (m=19 MiB, t=2, p=1): the batch of twenty sign-ins went from 4770 to 367 milliseconds on a 1 vCPU container |
| 3.4 | 14.9 | **Closed as a documentation matter (week 6)** — the division is deliberate and stated in section 6 of `docs/protocol.md`: the allow list **authorises**, and `IpRangePolicy` **guards the boundary** after DNS resolution on both sides, so a name that resolves to a private address is refused at connect time. No change to `validate_entry` |

In addition, two things were fixed in week 5 that have no matching line in the document but serve 14.13 and the
server's **availability**: moving argon2 off the event loop, and a concurrency ceiling for the derivation. Their detail
is in `docs/load-test-week5.md`.

---

## 1. Section 14 — the security requirements

| # | Item | State | Evidence / what is missing |
|---|---|---|---|
| 14.1 | Encrypting the connection between the two machines | Not the server's concern | The tunnel is TLS directly between the two clients. The server's role is to carry `cert_fp_sha256` between the two sides without decoding it; it checks its shape (64 lowercase hex) in `app/ws/protocol.py::_cert_fingerprint`. **A documented trust boundary:** the server knows the session secret and the certificate fingerprint, so a compromised server could in theory intercept the tunnel (an ADR in the plan's section 2; accepted in the MVP) |
| 14.2 | Encrypting the connection between the application and the server | Met | `deploy/Caddyfile` terminates TLS automatically and `deploy/docker-compose.yml` does not publish the API's port to the host (`expose: 8000` only) |
| 14.3 | Using HTTPS and WSS | Met | Caddy redirects 80→443 automatically and sends `Strict-Transport-Security: max-age=31536000; includeSubDomains`, and passes the WebSocket upgrade through. The contract requires `wss://` in section 1 of `ws-protocol.md`, and the token is **not put in the query string** |
| 14.4 | Using temporary session tokens | Met | The access JWT lasts 15 minutes with `typ=access` enforced (`app/core/security.py::decode_access_token`); the refresh token lasts 30 days **with rotation on every use**, and reuse detection revokes the device's whole chain (`app/services/tokens.py::rotate_refresh_token`); the session secret `secret_b64` lives only as long as the session |
| 14.5 | Not reusing session keys | Met | A fresh `secrets.token_bytes(32)` inside the single acceptance path (`app/services/requests.py::accept`), and one `session_keys` row per session. A new test: `tests/test_ws_sessions.py::test_every_session_gets_a_fresh_key_and_none_survives_the_end` |
| 14.6 | The host's consent to every session | Met | No path creates a `Session` except `requests.accept`, and it is restricted to the addressed device (`_load_pending(is_host=True)`, otherwise `forbidden`). There is no automatic acceptance and no "remember my consent" anywhere |
| 14.7 | The session ending automatically | Met | Two timers are armed on acceptance: the connect timeout and `expires_at` (`app/services/session_flow.py::schedule_timers`), they are re-armed after a restart (`reschedule_timers`), and a sweep at startup ends any session that survived (`sessions.end_dangling_sessions`) |
| 14.8 | Revoking the keys after the session ends | Met | `sessions.end_session` is **the only path** to termination, and it deletes the `session_keys` row and cancels both timers whatever the reason (a client, a disconnection, a timeout, an administrator, startup). Pinned in `tests/test_log_hygiene.py` and `tests/test_cli_sessions.py` |
| 14.9 | Stopping access to the host's local network | Not the server's concern (partly) | Enforcement is on the client after DNS resolution (`IpRangePolicy`, and week four's vulnerability 2). The server's role is what it **stores and serves** in the allow list: `allowlist.validate_entry` refuses any IP literal, the asterisk, and the slash. As for accepting names that resolve locally, that is deliberate and closed as a documentation matter — item 3.4 below |
| 14.10 | Stopping access to localhost | **Fixed in week 5** | `localhost` used to be a valid entry in the allow list. It and everything beneath it are now refused (RFC 6761 §6.3): `app/services/allowlist.py::LOOPBACK_NAME`. Tests in `tests/test_domains.py`. And on another path: the reachability probe and `POST /probe` refuse loopback, private and reserved addresses before opening any socket (`reachability_probe.forbidden_target_reason`, unwrapping the IPv4 embedded in 6to4, Teredo and mapped forms) |
| 14.11 | Stopping access to internal IP addresses | Met (the server's side) | An IP address cannot be entered in the allow list at all; and `session.endpoint`'s candidates are validated, stored canonically and never connected to by the server (`protocol.Candidate`). The final enforcement is on the client |
| 14.12 | Not storing passwords in the clear | Met | There is no plaintext password column in any model; only `users.password_hash`. Device secrets and refresh tokens are stored as SHA-256 and compared with `hmac.compare_digest` |
| 14.13 | Storing passwords with a secure hash | Met | argon2id through `argon2-cffi` with the OWASP parameters (m=19 MiB, t=2, p=1) adopted in [ADR-0007](decisions/0007-argon2-parameters.md), with `check_needs_rehash` and an automatic upgrade on the first successful sign-in. Verification always runs, even for a user who does not exist (`_DUMMY_HASH`), to level the timing, and it runs off the event loop with a concurrency ceiling. **The performance reservation (3.3) is closed** |
| 14.14 | Signing the Windows application | Not the server's concern | The build and distribution path |
| 14.15 | Distributing updates from a trusted source | Not the server's concern | The distribution path |
| 14.16 | Recording sign-in attempts | Met | `security_events` records `login_success` and `login_failed` (with the reason: `unknown_user`, `bad_password`, `device_secret_invalid`, `device_revoked`, `account_disabled`) and `login_locked` and `logout` and `refresh_reuse` and `device_revoked` and `session_admin_terminated`, all with the IP. `GET /admin/security-events` displays them |
| 14.17 | Rate limiting | **Met (completed in week 6)** | Three layers: **a frame budget per WebSocket connection** (100 frames/10 seconds, `app/ws/connection_manager.py::FRAME_BUDGET`) answering `error(rate_limited)` and dropping the frame without cutting the connection (added in week 5); and **four REST limits** by [ADR-0008](decisions/0008-rate-limit-policy.md): `/auth/login` keyed by IP (30/minute) **and keyed by email** (5 per 15 minutes, returned on success), and `/auth/refresh` (60/minute/IP), and `/probe` (10/minute per user); and the 15-minute account lockout after 10 failures as a last safety net. The detail is in 3.1 |
| 14.18 | Blocking suspicious devices | **Met (completed in week 6)** | By hand: revoking a device, by an administrator or by its owner, revokes the refresh tokens, clears the presence, **and closes the live control channel at once with code 4403** (`notify.close_device`). And automatically (ADR-0008): **a periodic task** on the existing timer schedule reads `security_events` and revokes the device on two conservative patterns (10 `device_secret_invalid` in an hour with no successful sign-in, or 3 `refresh_reuse`), by the same path and with an audit row explaining what fired it. And refresh-reuse detection still revokes the device's whole chain at once. The detail is in 3.2 |
| 14.19 | The ability to end a session centrally | **Fixed in week 5 (completed)** | `POST /admin/sessions/{id}/terminate` existed; `manage.py end-session` was added, going through the same path (`sessions.admin_terminate`), so it ends the row, deletes the key and writes the audit row with `via: "cli"`. `manage.py list-sessions` was added. An explicit warning in the output: the command-line version runs outside the API's process, so it does not send `session.terminate` to the two clients |
| 14.20 | Not decrypting HTTPS | Met by design | Not one byte of browsing passes through FastAPI; the server is a control plane only (technical decision 17). In the whole repository there is no outbound HTTP client except the reachability probe, **and it sends no byte**: it opens TCP, measures and closes (`reachability_probe.tcp_probe`) |
| 14.21 | Not logging browsing content | **Fixed in week 5** | No code was logging content, but `ENV=dev` set the root to DEBUG, and then **SQLAlchemy prints the queries with their bound parameters** — that is, the names of the sites browsed and presence addresses. `sqlalchemy`, `aiosqlite` and `asyncpg` were pinned above DEBUG whatever the application's level (`app/core/logging.py::STATEMENT_LOGGERS`). And `RedactingFilter` replaces the value of every key carrying `password`/`secret`/`token`/`authorization`/`cookie`. The new test `tests/test_log_hygiene.py` drives a full session cycle and then searches every emitted log with the same patterns as `scripts/security/check-logs-clean.sh` plus two more |

---

## 2. Section 15 — the privacy requirements

### 2.1 What is shown to the host before they accept the request

The server is responsible for **supplying** the client with these fields in `request.incoming`; the display itself is
track C's.

| # | Item | State | Evidence |
|---|---|---|---|
| 15.1 | The requesting user's name | Met | `request.incoming.guest_name` from `users.display_name` |
| 15.2 | The device's name | Met | `request.incoming.guest_device` from `devices.name` |
| 15.3 | The requested connection duration | Met | `request.incoming.duration_min`, validated against `max_session_minutes` before the request is created |
| 15.4 | The allowed sites or categories | Met | `request.incoming.allowlist_version` + `GET /domains` (with an ETag and a specific-version request). The host sees **the same version** that will be applied, not a list that may be stale |
| 15.5 | The ability to disconnect | Met | `session.end` is accepted **from either side** in both the `connecting` and `active` states, and cutting the control channel ends the session at once with `host_disconnected` |
| 15.6 | A warning that the sites will see the host's address | Not the server's concern | Text in the client's interface. The server supplies what it needs: `session.created.peer_public_ip` and `same_public_ip` |

### 2.2 What the usage policy sets out

| # | Item | State | Note |
|---|---|---|---|
| 15.7 | Prohibiting illegal activity | Not the server's concern | Policy text (product/legal) |
| 15.8 | Not sharing the connection with people who are not trusted | Not the server's concern | Policy text. It is technically supported by the local proxy accepting the work browser alone (the PID check, plan decision 2) |
| 15.9 | Abiding by the sites' terms | Not the server's concern | Policy text |
| 15.10 | The user's responsibility for the session | Met (by the record) | Every session is bound to all four users and devices in `sessions`, and is never deleted |
| 15.11 | The company's responsibility for determining the allowed sites | Met | The list is central and governed by an administrator: `PUT /admin/domains` validates **every** entry and refuses the whole request on any error, and publishes an immutable version (`allowlist_versions`) and broadcasts `allowlist.updated`. No path lets an ordinary user widen the list |
| 15.12 | Not logging browsing content | Met / hardened | See 14.21. No path in the server receives a URL, a header or a request body at all: the widest thing that arrives is `session.end.domains` (hostnames only) |
| 15.13 | Recording administrative session data only | Met | The `sessions` row stores: the two parties, the times, the end reason, the byte totals, and the connect result, the winning candidate's type and the TLS version. **No content.** Domain names are stored in `session_domains` only when `log_domains` is on (the default is **false**), and they are normalised and capped at 200 distinct domains, and never written to the log. And, importantly for transparency: the value of `log_domains` is sent to every client in `hello.ack.settings`, so the host knows in advance whether the domains will be stored |

---

## 3. The reservations — all of them now closed

### 3.1 Rate limiting covers neither `/auth/refresh` nor `/probe`, and the sign-in limit is by IP only — **closed**

**What it was:** `POST /auth/login` was limited to 5/minute **per IP** only. A whole team behind one NAT shares the
quota (a legitimate annoyance), and in return twenty different addresses get past the limit entirely — and week five's
measurement showed twenty concurrent sign-ins raising the session cycle from 78 ms to 10.7 seconds on one core
(`docs/load-test-week5.md` section 5.2). And `POST /auth/refresh` was unlimited and unauthenticated, and
`POST /probe` was authenticated and unlimited and made the server open TCP to any public address the user asked for — a
slow port scanner wearing the server's identity.

**What was implemented ([ADR-0008](decisions/0008-rate-limit-policy.md), week 6):** four token buckets, all answering
with the existing `rate_limited` envelope and a `Retry-After`, and all disabled by the existing `RATE_LIMIT_ENABLED`
key:

| Path | Key | Limit |
|---|---|---|
| `POST /auth/login` | the caller's address | 30/minute |
| `POST /auth/login` | **the submitted email** (SHA-256 hashed) | 5 per 15 minutes, **the token is returned on success**, so only failure consumes the budget |
| `POST /auth/refresh` | the caller's address | 60/minute |
| `POST /probe` | `user_id` | 10/minute |

The calibration against **the account lockout** is explicit: the email bucket's capacity (5) is **below the lockout
threshold (10)**, so one burst cannot lock an account however fast it goes — and the lockout is a denial of service an
attacker can trigger, so the rate limit had to bite first. And reaching ten failed attempts now takes at least a
quarter of an hour, and leaves a clear trace in `GET /admin/security-events`. And the coherence with
`MAX_CONCURRENT_KDF` is documented in ADR-0008: the limits govern **the arrival rate** (so what is refused costs no
derivation at all), and the ceiling governs the derivation's **concurrency** and its peak memory (~38 MiB with the
ADR-0007 parameters).

**A documented value changed (it needs the owner of `docs/api.md`):** the "5 requests/minute/IP" line became 30, and
three new limits came with it. No shape in the contract changed.

The tests: `tests/test_rate_limit.py` (15 tests with the limits **enabled**), which state explicitly that an office
behind one NAT is unaffected by a colleague's mistake, and that a burst never reaches the lockout.

### 3.2 No automatic detection of a "suspicious" device (14.18) — **closed**

**What it was:** blocking was manual (revoking a device) or indirect (the account lockout, refresh-reuse detection),
and the signals were all in `security_events` and unconsumed, and `listener_unauthenticated` was a mere `TODO`.

**What was implemented ([ADR-0008](decisions/0008-rate-limit-policy.md) part two):**

1. **`listener_unauthenticated` is now implemented** against the contract pinned in `docs/api.md`: a `data` object in
   `POST /diagnostics` carrying the key with a positive numeric value writes a `security_events` row alongside the
   usual diagnostic row, with `listener_port` and `unauthenticated_peers` (≤10). The consumption is in
   `services/diagnostics.store` rather than in the route, so there is no way to store such a payload without raising
   the signal. **No payloads and no domain names:** anything that is not an IP address is dropped, and the counter is
   bounded.
2. **A periodic task** on **the same existing timer schedule** (`session_timer.scheduler`, the key
   `device-risk-sweep`) — no second scheduling mechanism — with two conservative rules:
   - **10** `login_failed` events with the reason `device_secret_invalid` for the same device within **60 minutes**,
     **and no** `login_success` for that device inside the window.
   - **3** `refresh_reuse` events for the same device inside the window (not one: the first reuse revokes the whole
     chain, so a token that was in flight may bounce back).
3. Every automatic revocation goes through the same `services.devices.revoke_device`, so it revokes the refresh
   tokens, clears the presence, **writes a `device_revoked` row explaining what fired it**
   (`{"by": "auto", "rule": …, "matches": …, "window_minutes": …}`), **and closes the live control channel with code
   4403**. It appears in `GET /admin/security-events`, and it is idempotent.
4. The thresholds are all settings (`DEVICE_RISK_*`), their defaults are far above what ordinary use generates, a rule
   is disabled with a zero and the whole detection with one key — because a wrong revocation cuts a real user off from
   their device.

**Signals that deliberately do not revoke a device:** `listener_unauthenticated` (it describes the network around the
host, not its behaviour; if we revoked on it, any port scanner could cut an employee off from their computer), and
`bad_password` (it concerns an account, not a device, and its handling is the email limit then the lockout).

The tests: `tests/test_device_risk.py` (16 tests: every rule fires, every rule does **not** fire just short of the
threshold, the revocation is idempotent, and the live channel is closed with 4403), and `tests/test_diagnostics.py`
for consuming the signal.

### 3.3 The argon2 parameters against a single core (14.13) — **closed in week 5**

The old parameters (t=3, m=64 MiB, p=4) cost ~210 ms of CPU time per operation on the target VPS.
**[ADR-0007](decisions/0007-argon2-parameters.md) was adopted on 2026-09-05** with the product owner choosing the
OWASP parameters (m=19 MiB, t=2, p=1) and they were implemented: the batch of twenty sign-ins on a 1 vCPU container
went from **4770 to 367 milliseconds** (about thirteen times), and a single derivation from ~210 to ~16 milliseconds.
Existing accounts stay valid and are upgraded automatically at the first successful sign-in (`check_needs_rehash`),
pinned by two tests in `tests/test_security.py`.

### 3.4 The allow list accepts names that resolve inside the network (14.9) — **closed as a documentation matter, with no code change**

An entry such as `intranet:80` or `portal.corp:8443` remains **deliberately accepted**, and the decision is now
documented in section 6 of `docs/protocol.md`:

> The list is an **authorisation**, not a guarantee of reachability: it determines which names may be requested. The
> actual security boundary is rule 6 (`IpRangePolicy`), and it is applied **after the name is resolved and before any
> socket is opened**, on both sides: the host in `EgressPolicy` and the user on the direct path in
> `ConnectProxyServer`.

So a name in the list that resolves to an internal address **is refused at connect time** with
`OPEN_FAIL(private_ip)`. Refusing it at load time instead would break the `docs/api.md` contract **with no security
gain**, because any public name may point at a private address, so the check at resolution is necessary either way.
`validate_entry` stays as it is (and still refuses `localhost` and everything beneath it, IP literals, the asterisk and
the slash).

### 3.5 (A small note, **still open**) Query parameters appear in the access log
`uvicorn.access` records the full path, and `GET /admin/users?q=…` may carry an email. It is not a secret nor browsing
content, but it is personal data in a text log. It was never a reservation against an item of sections 14 and 15 — and
that is why it stayed open while the four reservations were closed. It is dealt with by dropping the access log or
stripping the query at the first cleanup.

---

## 4. What must be verified on the deployed server (no test can prove it)

| # | The verification | How |
|---|---|---|
| 1 | Real TLS and HSTS | `curl -sI https://<domain>/healthz` returns 200 and a `Strict-Transport-Security` header; and `curl -sI http://<domain>/` returns a redirect to 443 |
| 2 | The API is not published directly | From outside the server: no response on 8000; `ufw status` shows 80 and 443 only; PostgreSQL with no published port |
| 3 | `ENV=prod` in earnest | `GET /docs` with no token returns 401 rather than a Swagger page; and so does `GET /openapi.json` |
| 4 | `JWT_SECRET` is a real secret | ≥ 32 random bytes and not the text from `.env.example`; and it does not appear in `docker inspect` to anyone unauthorised |
| 5 | The log is clean after a real session | `scripts/security/check-logs-clean.sh` against `docker compose logs api` after a full session between two Windows machines |
| 6 | `session_keys` is empty after termination | `scripts/security/check-session-keys.sh` (which is already an item in `docs/acceptance-checklist.md`) |
| 7 | **No browsing data passes through the API container, and the server cannot read what passes through the relay** | While a video plays inside the session: `docker stats api` stays at the heartbeat level, while `docker stats relay` rises — and the bytes there are opaque, since the TLS handshake, the certificate pinning and `AUTH1`/`AUTH2` run **between the two machines, inside** the relay's stream. The relay's log carries the session id and the sizes, **with not one domain name** (success criterion 16, [ADR-0009](decisions/0009-relay-default.md)) |
| 8 | The backups and their permissions | The `/backups` files are not world-readable, and a periodic restore exercise (carried out in week 4) |
| 9 | Clock discipline | All of section 5's timers derive from `expires_at`; clock drift means sessions ending early or late. Confirm `systemd-timesyncd`/`chrony` |
| 10 | fail2ban on 443 | Mentioned in `docs/runbook.md`; confirm its rules actually watch Caddy's log |
| 11 | The per-IP sign-in limit's effect | From one address with a different email each time: the **thirty-first** attempt within a minute returns 429 with a `Retry-After` header (ADR-0008) |
| 12 | The per-account sign-in limit's effect | From **different addresses** against the same email with a wrong password: the sixth attempt returns 429 and `Retry-After: 180`, and the account **is not locked** (`failed_logins` stays at 5) |
| 13 | The per-user `/probe` limit | With one token: the eleventh request within a minute returns 429 even from another address |
| 14 | The automatic detection works and does not overreach | `GET /admin/security-events?type=device_revoked` after a week of operation: every `by: "auto"` row must have an understandable cause. **Zero automatic revocations on a healthy fleet is the expected result**; any unjustified automatic revocation means raising the threshold in `DEVICE_RISK_*` |

---

## 5. Test coverage

### 5.1 Added in week 5

| File | What it proves |
|---|---|
| `tests/test_log_hygiene.py` | A full session cycle with `log_domains=true` writes no browsed domain, no URL, no header, no token and no `secret_b64` into the log — with the same patterns as `check-logs-clean.sh`, and at DEBUG level (the most verbose the application sets). And it proves the stored row is administrative data only, and that the domains are never stored when `log_domains=false` |
| `tests/test_ws_sessions.py` | Every session gets a fresh 32-byte key, the two keys differ, and no `session_keys` row survives termination |
| `tests/test_ws_hello.py` | The frame budget answers `rate_limited` on the excess frame only, does not cut the connection, and is per connection rather than shared |
| `tests/test_domains.py` | `localhost`, `app.localhost`, `=localhost` and `localhost:8080` are refused |
| `tests/test_cli_sessions.py` | `end-session` ends the row, deletes the key and writes an audit row with `via: "cli"`, and refuses an unknown or already-ended session |
| `tests/test_ws_presence.py` | The broadcast does not happen with no change, and even so every recipient keeps their correct view |

### 5.2 Added in week 6

| File | What it proves |
|---|---|
| `tests/test_rate_limit.py` | The four limits with limiting **enabled**: one account is not attacked from twenty addresses, a burst **never reaches the account lockout** (`failed_logins = 5 < 10`), an office behind one NAT is unaffected by a colleague's mistakes and signs ten employees in one after another, a successful sign-in **does not consume** the email budget, the per-user `/probe` limit is not evaded by changing address, 401 precedes 429 on the authenticated path, and the bucket returns one token and no more |
| `tests/test_device_risk.py` | Every rule fires at its threshold and does **not** fire at the threshold minus one; a successful sign-in inside the window clears the suspicion; events outside the window are not counted; the count is per **device** and not per user; the revocation is idempotent (one audit row, not two); it revokes the refresh tokens; it closes the live channel with **4403**; `listener_unauthenticated` and `bad_password` **never revoke a device**; and the thresholds can be set and disabled |
| `tests/test_diagnostics.py` | Consuming `listener_unauthenticated`: a diagnostic row and a security row together, the boolean `true` is not counted as a number, the peers are capped at ten and canonical and deduplicated, and **anything that is not an IP address is dropped** (a domain name or a URL in the payload does not reach `security_events`), and the signal is visible in `GET /admin/security-events` |
| `tests/test_openapi.py` | Every path in the generated document carries an **explicit** `summary` (not the name FastAPI invents) and a real description, documents its errors, every error is rendered with the shared envelope (so no FastAPI `HTTPValidationError` is left), every request/response model has an example, and every tag has a description |
