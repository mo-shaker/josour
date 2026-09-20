# The REST contract: `/api/v1`

Version: 1 (frozen at the end of week 1). The executable source is the OpenAPI generated at `/docs` (restricted to
administrators in production). This file pins the shapes track C depends on.

## General rules

- JSON in the request and the response. Times are ISO-8601 UTC with a `Z` suffix. Identifiers are UUIDs.
- Authentication: `Authorization: Bearer <access>`. The access JWT is valid for 15 minutes, the refresh token for 30
  days and it is replaced on every refresh (rotation; reusing an old refresh token revokes the whole chain).
- Errors: `{ "error": { "code": "…", "message": "…" } }` with an appropriate HTTP status. Common codes:
  `invalid_credentials`, `account_locked`, `account_disabled`, `device_revoked`, `unauthorized`, `forbidden`,
  `not_found`, `validation_error`, `rate_limited`, `conflict`.
- **Rate limiting** (recalibrated in week 6, [ADR-0008](decisions/0008-rate-limit-policy.md)); exceeding it returns
  `429` with the `rate_limited` envelope and a `Retry-After` header:

| Path | Key | Limit |
|---|---|---|
| `POST /auth/login` | IP address | 30/minute |
| `POST /auth/login` | **the submitted email** (hashed) | 5 per 15 minutes, **returned on a successful sign-in** |
| `POST /auth/refresh` | IP address | 60/minute |
| `POST /probe` | `user_id` | 10/minute |

  The email-keyed limit is what protects the account: the IP limit alone is got past by twenty different addresses,
  and in return it punishes a whole team behind one NAT. Its capacity (5) is deliberately **below the account-lockout
  threshold** (10 failures), so no burst can lock an account: the lockout is a denial of service an attacker can
  trigger. And returning the credit on success makes failures alone consume the quota.
- The account locks for 15 minutes after 10 failed attempts → `423 account_locked`.

## Authentication

### `POST /auth/login`
```json
{ "email": "a@b.c", "password": "…",
  "device": { "id": "uuid|null", "secret": "…|null", "name": "LAPTOP-01", "os_version": "Windows 11 Pro", "os_build": "22631" } }
```
- `device.id` and `device.secret` are empty on the machine's first sign-in → the server creates the device and returns
  `device_secret` **once**; the client stores it with DPAPI.
- An existing device with the wrong secret → `401 unauthorized`. A revoked device → `403 device_revoked`.

The `200` response:
```json
{ "access_token": "…", "refresh_token": "…", "expires_in": 900,
  "user": { "id": "…", "email": "…", "display_name": "…", "role": "user" },
  "device": { "id": "…", "name": "…", "secret": "…|null" } }
```

### `POST /auth/refresh`  `{ "refresh_token": "…" }` → the same response shape as above, without `device.secret`.
### `POST /auth/logout`  `{ "refresh_token": "…" }` → `204`.

## The current user

- `GET /me` → `{ id, email, display_name, role }`
- `GET /me/devices` → `[{ id, name, os_version, status, last_seen_at, created_at }]`
- `DELETE /me/devices/{id}` → `204` (deregistration; revokes the device's refresh tokens)
- `GET /hosts` → `[{ device_id, user_display_name, device_name, reachable }]` — the same shape as `hosts.snapshot` and
  exactly the same filtering (connected, "available" enabled, with no unfinished session, **and excluding the user's
  own devices**; pinned in week 3)
- `GET /sessions/me?limit=50` → `[{ id, role, peer_display_name, peer_device_name, status, created_at, started_at, ended_at, end_reason, bytes_up, bytes_down }]`
- `GET /domains` → `{ "version": 3, "entries": ["example.com", "=exact.com", "portal.corp:8443"] }` with `ETag: "3"`;
  supports `If-None-Match` → `304`, and `?version=N` returns a specific version or `404`.

  **Version retention (pinned in week 5):** the list's versions are immutable snapshots and are never deleted. The
  dependence on them is real: the host calls `?version=N` to show the requester the sites that will actually be granted
  before accepting (a requirement of section 15 of the product document), and deleting an old version makes that
  disclosure fail. Any future cleanup must exclude versions referenced by unfinished sessions.

## The technical prototype

- `POST /probe`  `{ "ip": "…", "port": 12345 }` → `{ "reachable": true, "latency_ms": 42 }` (on failure:
  `{ "reachable": false, "latency_ms": null }`). Requires authentication. Private and loopback addresses are refused
  with `400 validation_error`. The server tries a TCP connection with a 3-second timeout and closes it at once. Used in
  the week-1 prototype and in reachability probing.
- `POST /diagnostics`  `{ "session_id": "uuid|null", "role": "guest|host|null", "data": { … } }` → `201 { "id": "…" }`.
  Requires authentication (any role). Stores a row in `connect_diagnostics` for the sender's device (from the token).
  `data` is a free JSON object of at most 64 KB once serialised, otherwise `422 validation_error`. When `session_id` is
  passed the session must exist (`404 not_found`) and the user must be one of its two parties (`403 forbidden`). The
  week-2 NAT prototype tool uses it to upload the ten pairs' results; the summary is through `GET /admin/diagnostics`.

  **Reserved keys inside `data` (pinned in week 6):** if the object carries the key `listener_unauthenticated` with a
  positive numeric value, the server also reads it as a security signal and writes a `security_events` row of type
  `listener_unauthenticated` alongside the usual diagnostic row. Its context: the host's listener during the connect
  window closes any connection that does not pass `AUTH1` (the channel protocol, section 2), and that is the only
  moment the system sees an unauthorised attempt to reach the host's port — and the server cannot observe it itself.
  The optional accompanying keys: `listener_port` (a number), `unauthenticated_peers` (an array of IP addresses, at
  most 10), and `unauthenticated_peers_distinct` (the number of distinct sources before truncation — it distinguishes a
  scan from dozens of sources from repeated attempts from one, which the truncated list alone does not reveal). **No
  payloads and no domain names are recorded** — a counter and source addresses only.

  **A counter of zero is not a signal:** the server writes a security event only for a positive value. Whether the
  request is sent at all is the sender's decision: **the application** does not send on zero because it has nothing to
  say, while **the prototype tool** sends its full diagnostics on every run, where a zero is a statement rather than
  silence. The denominator (how many sessions saw nothing) is derived from the sessions table, so it is lost under
  neither behaviour.

- `GET /healthz` (unauthenticated, and outside `/api/v1`) → `{ "status": "ok", "product":"josour", "version": "0.1.0" }`.

  The structure is part of the contract even though it is outside OpenAPI: the client's first-run and settings screens
  decide from it **whether this is a Josour server** before letting the user continue, and without the product marker a
  wrong address would come back later in the shape of "wrong password". It is not a security control — the response is
  unauthenticated and easy to imitate — but a way to fail early and clearly.

## Administration (role = admin)

- `POST /admin/users` `{ email, password, display_name, role }` → `201` the user.
- `GET /admin/users` → a list. `PATCH /admin/users/{id}` `{ is_active?, password?, display_name?, unlock?: true }`.
  - `password` and `is_active: false` both revoke the refresh tokens.
  - **`is_active: false` takes effect at once**: it clears the user's presence and closes every live control channel of
    theirs with code `4403`, as revoking a device does. Without that, revoking the tokens alone left a disabled account
    browsing over someone else's connection until its access token expired. A `user_deactivated` row is written, naming
    the administrator who did it.
  - The devices themselves are **not revoked**: it is the account that is disabled, not the hardware, so reactivation
    does not leave the user registering their devices again.
  - There is no deleting a user: `sessions.guest_user_id` and `host_user_id` are both `ON DELETE CASCADE`, so deleting
    a user erases the record of every session they were a party to — **including the other host's share of it**.
- `GET /admin/devices?user_id=`, `POST /admin/devices/{id}/revoke` → `204`.
- `GET /admin/domains` → `{ version, entries, updated_at }`.
- `PUT /admin/domains` `{ "entries": [...] }` → a new version; validates every entry; broadcasts `allowlist.updated`.
- `GET /admin/sessions?status=&limit=`, `POST /admin/sessions/{id}/terminate` → `204`.
- `GET /admin/security-events?limit=`, `GET /admin/diagnostics` → a summary: the number of sessions, the
  `connect_result=ok` rate (with `timeout` counted as a failure), the distribution of `winner_type`, of `tls_version`,
  and of `end_reason` (added in week 4).
- `GET /admin/settings`, `PATCH /admin/settings` `{ max_session_minutes?, request_timeout_seconds?, log_domains?, enforce_allowlist?, allowed_ports? }`.

## The settings' default values

| Key | Default |
|---|---|
| `max_session_minutes` | 120 |
| `request_timeout_seconds` | 60 |
| `connect_timeout_seconds` | 30 |
| `log_domains` | false |
| `enforce_allowlist` | false — **[ADR-0010](decisions/0010-route-all-through-host.md)**: everything the work browser asks for goes through the host. The list restricts only when an administrator turns this key on |
| `allowed_ports` | `[80, 443]` |
