# The REST contract: `/api/v1`

Version: 1 (frozen at the end of week 1). The executable source is the OpenAPI generated at `/docs` (restricted to
administrators in production). This file pins the shapes track C depends on.

## General rules

- JSON in the request and the response. Times are ISO-8601 UTC with a `Z` suffix. Identifiers are UUIDs.
- Authentication: `Authorization: Bearer <access>`. The access JWT is valid for 15 minutes, the refresh token for 30
  days and it is replaced on every refresh (rotation; reusing an old refresh token revokes the whole chain).
- Errors: `{ "error": { "code": "…", "message": "…" } }` with an appropriate HTTP status. Common codes:
  `invalid_credentials`, `account_locked`, `account_disabled`, `device_revoked`, `unauthorized`, `forbidden`,
  `not_found`, `validation_error`, `rate_limited`, `conflict`. Added on 2026-10-13: `account_pending`,
  `account_rejected`, `registration_closed`, `registration_unavailable`, `upgrade_required`.
- **Rate limiting** (recalibrated in week 6, [ADR-0008](decisions/0008-rate-limit-policy.md)); exceeding it returns
  `429` with the `rate_limited` envelope and a `Retry-After` header:

| Path | Key | Limit |
|---|---|---|
| `POST /auth/login` | IP address | 30/minute |
| `POST /auth/login` | **the submitted email** (hashed) | 5 per 15 minutes, **returned on a successful sign-in** |
| `POST /auth/refresh` | IP address | 60/minute |
| `POST /probe` | `user_id` | 10/minute |
| `POST /auth/register` | IP address | 5/hour (added 2026-10-13, [ADR-0017](decisions/0017-self-registration-with-approval.md)) |
| `POST /contacts/invites` | `user_id` | 20/day (added 2026-10-13, [ADR-0014](decisions/0014-contacts-and-server-held-trust.md)) |
| `POST /diagnostics` | `user_id` | 60/hour (added 2026-10-28: every call stores up to 64 KB) |

  The email-keyed limit is what protects the account: the IP limit alone is got past by twenty different addresses,
  and in return it punishes a whole team behind one NAT. Its capacity (5) is deliberately **below the account-lockout
  threshold** (10 failures), so no burst can lock an account: the lockout is a denial of service an attacker can
  trigger. And returning the credit on success makes failures alone consume the quota.
- The account locks for 15 minutes after 10 failed attempts → `423 account_locked`. Since 2026-10-28 a wrong
  second-factor code in the web dashboard counts as a failed attempt too, and in the dashboard the failure counter is
  cleared only once the code has passed, not by the password alone.
- **Concurrent writes** (added 2026-10-28). Two requests that would write the same unique row at the same moment
  never produce a `500`. The ones with a documented answer are retried once and get it: the same address registered
  twice → both `202 pending` in `approval` mode (`409 conflict` in `open` mode); the same invitation sent twice → both
  `202 {}`; two people accepting each other's invitations → both `200`. Anything else answers `409 conflict`.

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

- **Added on 2026-10-13** ([ADR-0017](decisions/0017-self-registration-with-approval.md)): after a **correct**
  password only, `403 account_pending` while a self-registration waits for approval and `403 account_rejected` when it
  was turned down. A wrong password always answers `401 invalid_credentials`, so neither code reveals that an address
  registered.
- `426 upgrade_required` when the `Josour/<version>` User-Agent is older than the `min_client_version` setting.
  A request without a Josour User-Agent is not steered.

### `POST /auth/refresh`  `{ "refresh_token": "…" }` → the same response shape as above, without `device.secret`.

The rotation is a conditional write: two refreshes presenting the **same** token at the same moment cannot both rotate
it. One gets `200`; the other is treated as a reuse (pinned 2026-10-28) — `401 unauthorized`, every live refresh token
of that device revoked (the winner's new one included) and a `refresh_reuse` security event, exactly as for a late
reuse. A client must therefore never refresh in parallel.
### `POST /auth/logout`  `{ "refresh_token": "…" }` → `204`.

### Self-registration (added on 2026-10-13, [ADR-0017](decisions/0017-self-registration-with-approval.md))

- `GET /auth/registration` (unauthenticated) → `{ "mode": "closed" | "approval" | "open" }`. The app shows
  "Create account" unless it is `closed`.
- `POST /auth/register` `{ email, password (≥ 8), display_name }` — never signs in; the app calls `POST /auth/login`
  once the account may be used, and registers its device then.
  - `approval` (the default): `202 { "status": "pending" }` — **identical whether or not the address already had an
    account**, so registering cannot be used to learn who uses the server (the attempt on a taken address is written
    as `registration_duplicate`). Above `MAX_PENDING_REGISTRATIONS` (200) waiting accounts:
    `429 registration_unavailable`.
  - `open`: `201 { "status": "active" }`; a taken address is `409 conflict` — that mode cannot hide it.
  - `closed`: `403 registration_closed`.

## Contacts (added on 2026-10-13, [ADR-0014](decisions/0014-contacts-and-server-held-trust.md))

Only contacts can see each other's hosts and ask for or offer a connection.

- `GET /contacts` → `[{ user_id, display_name, email, online, i_trust, trusts_me, since }]`. `i_trust`: I let this
  contact use **my** connection without asking me; `trusts_me`: they let me use theirs.
- `PUT /contacts/{user_id}/trust` `{ "auto_accept": bool }` → the contact. About the caller's own connection only;
  withdrawing it never ends a session already running.
- `DELETE /contacts/{user_id}` → `204`. Removes both directions; pending questions between the two are settled and a
  live session between them ends with `contact_removed`.
- `GET /contacts/invites` → `{ incoming: [{ id, from_user_id, from_display_name, from_email, created_at, expires_at }],
  outgoing: [{ id, email, status: "pending"|"expired", created_at, expires_at }] }`. A **decline is never shown** to
  the inviter: it looks exactly like an address with no account.
- `POST /contacts/invites` `{ email }` → always `202 {}`, whether or not the address has an account. Inviting someone
  who already invited you makes you contacts at once. An address without an account receives the invitation if it
  registers and is approved before `invite_ttl_days` pass.
- `POST /contacts/invites/{id}/accept` → `{ contact }`; `POST /contacts/invites/{id}/decline` → `204`;
  `DELETE /contacts/invites/{id}` (the inviter withdrawing) → `204`.

## The current user

- `GET /me` → `{ id, email, display_name, role }`
- `GET /me/devices` → `[{ id, name, os_version, status, last_seen_at, created_at }]`
- `DELETE /me/devices/{id}` → `204` (deregistration; revokes the device's refresh tokens)
- `GET /hosts` → `[{ device_id, user_id, user_display_name, device_name, reachable }]` — the same shape as
  `hosts.snapshot` and exactly the same filtering (connected, "available" enabled, with no unfinished session, **and
  excluding the user's own devices**; pinned in week 3). Since 2026-10-13 only the hosts of the caller's **contacts**
  are listed, and each entry carries `user_id`.
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
  Requires authentication (any role); 60 an hour per user, then `429 rate_limited`. Stores a row in
  `connect_diagnostics` for the sender's device (from the token).
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

- `GET /healthz` (unauthenticated, and outside `/api/v1`) → `{ "status": "ok", "product":"josour", "version": "2.0.0" }`.

  The structure is part of the contract even though it is outside OpenAPI: the client's first-run and settings screens
  decide from it **whether this is a Josour server** before letting the user continue, and without the product marker a
  wrong address would come back later in the shape of "wrong password". It is not a security control — the response is
  unauthenticated and easy to imitate — but a way to fail early and clearly.

## Administration (role = admin)

- `POST /admin/users` `{ email, password, display_name, role }` → `201` the user (approved and active). A
  `user_created` security event names the role and the administrator (added 2026-10-28; `manage.py create-user`
  writes it with `via: "cli"`).
- `GET /admin/users?q=&state=&role=&limit=&offset=` → a list; `state` is `active`, `disabled`, `pending`, `rejected`
  or `deleted`, and each row carries `state`, `approval_status`, `registered_via`, `approved_at`, `deleted_at` and
  `last_login_at` (added 2026-10-13). `PATCH /admin/users/{id}`
  `{ is_active?, password?, display_name?, email?, role?, unlock?: true }` (`email` and `role` added 2026-10-13).
  - `password` and `is_active: false` both revoke the refresh tokens.
  - Every change is audited (added 2026-10-28): `user_updated` with `details` carrying `email_changed`, `role`,
    `password_set`, `activated` and/or `unlocked`, plus `actor_user_id`; deactivation is `user_deactivated`.
  - **`is_active: false` takes effect at once**: it clears the user's presence and closes every live control channel of
    theirs with code `4403`, as revoking a device does. Without that, revoking the tokens alone left a disabled account
    browsing over someone else's connection until its access token expired. A `user_deactivated` row is written, naming
    the administrator who did it.
  - The devices themselves are **not revoked**: it is the account that is disabled, not the hardware, so reactivation
    does not leave the user registering their devices again.
  - A pending or rejected account cannot be activated this way (`409`) — approve it instead. Nobody may disable or
    demote themselves, nor the last active administrator (`409`). Two administrators disabling or demoting each
    other at the same moment cannot both succeed: the second gets `409`.
- `POST /admin/users/{id}/approve`, `POST /admin/users/{id}/reject` → the user (added 2026-10-13). Approving delivers
  the contact invitations that were waiting for the address.
- `DELETE /admin/users/{id}` → `204` (added 2026-10-13). A **soft** delete: email, name and password are replaced,
  every device is revoked and disconnected, contacts and invitations go, and a live session ends `admin_terminated`.
  The row stays, because `sessions.*_user_id` are `ON DELETE CASCADE` and a hard delete would erase the other party's
  history too.
- `GET /admin/users/{id}/contacts` → `[{ user_id, contact_user_id, contact_display_name, contact_email, auto_accept,
  trusted_by_contact, created_at }]`; `DELETE /admin/contacts/{user_id}/{contact_user_id}` → `204`;
  `POST /admin/contacts/{user_id}/{contact_user_id}/revoke-trust` → `204` — an administrator can withdraw a trust
  grant, never give one. `GET /admin/invites?status=&limit=` → the invitations (added 2026-10-13).
- `GET /admin/devices?user_id=`, `POST /admin/devices/{id}/revoke` → `204`.
- `GET /admin/domains` → `{ version, entries, updated_at }`.
- `PUT /admin/domains` `{ "entries": [...] }` → a new version; validates every entry; broadcasts `allowlist.updated`.
- `GET /admin/sessions?status=&limit=`, `POST /admin/sessions/{id}/terminate` → `204`.
- `GET /admin/security-events?limit=`, `GET /admin/diagnostics` → a summary: the number of sessions, the
  `connect_result=ok` rate (with `timeout` counted as a failure), the distribution of `winner_type`, of `tls_version`,
  and of `end_reason` (added in week 4).
- `GET /admin/settings`, `PATCH /admin/settings` `{ max_session_minutes?, request_timeout_seconds?, connect_timeout_seconds?, log_domains?, enforce_allowlist?, allowed_ports?, registration_mode?, min_client_version?, invite_ttl_days? }`.
  Every change is written as a `settings_changed` security event.
- **The web dashboard** (`/admin`, [ADR-0016](decisions/0016-web-admin-dashboard.md)) calls the same services, so it
  writes the same security events; each one it writes for an administrative action also carries `details.via: "web"`
  (`user_created`, `user_updated`, `user_deactivated`, `user_approved`, `user_rejected`, `user_deleted`,
  `device_revoked`, `contact_removed`, `trust_revoked`, `settings_changed`, `session_admin_terminated`, `totp_enabled`,
  `totp_disabled`). Its own sign-in events (`admin_web_login_*`, `admin_web_logout`) are web-only by type. A published
  allow-list is recorded in `allowlist_versions.created_by` rather than as a security event, from the API and the
  dashboard alike.

## The settings' default values

| Key | Default |
|---|---|
| `max_session_minutes` | 120 |
| `request_timeout_seconds` | 60 |
| `connect_timeout_seconds` | 30 |
| `log_domains` | false |
| `enforce_allowlist` | false — **[ADR-0010](decisions/0010-route-all-through-host.md)**: everything the guest's browsers ask for goes through the host. The list restricts only when an administrator turns this key on |
| `allowed_ports` | `[80, 443]` |
| `registration_mode` | `approval` — [ADR-0017](decisions/0017-self-registration-with-approval.md) |
| `min_client_version` | `""` (no floor) |
| `invite_ttl_days` | 30 |
