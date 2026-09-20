# The WebSocket contract: `/ws`

Version: 1 (frozen at the end of week 1). Any change goes through review by tracks A and C, and this file is updated
before the code.

## 1. Connecting and authenticating

- The path: `wss://<host>/ws` (with no parameters in the URL; the token is not put in the query string).
- The messages are JSON text, one object per WebSocket frame. The encoding is UTF-8.
- The client's first message **must** be `hello` within 5 seconds of the connection opening, otherwise the server
  closes with code `4401`.
- One connection per device: a new connection with the same `device_id` closes the old one with code `4409`.
- The heartbeat: the server sends `ping` every 20 seconds; the client replies `pong` at once. The client may also send
  `ping` and the server replies `pong`. Losing two consecutive beats (40 seconds) = a disconnection.
- A disconnection is acted on at once: `presence.connected=false`, and any unfinished session for that device is ended
  with `guest_disconnected` or `host_disconnected` and `session.terminate` is sent to the other side.

## 2. The envelope

Every message is a JSON object with a mandatory `type` field. Client messages that expect a reply carry a `ref` (a
string the client generates, unique within the connection). The server replies either with the expected message or
with an `error` carrying the same `ref`.

```json
{ "type": "request.create", "ref": "c1", "host_device_id": "…", "duration_min": 30 }
{ "type": "error", "ref": "c1", "code": "host_unavailable", "message": "…" }
```

The `error` codes: `unauthorized`, `bad_request`, `not_found`, `host_unavailable`, `session_exists`,
`request_pending`, `forbidden`, `rate_limited`, `internal`.

`rate_limited` is also used on the channel itself (pinned in week 5): every connection has a frame budget (100 frames
in 10 seconds); exceeding it returns `rate_limited` **without dropping the connection**, protecting the single worker
from an authenticated device flooding the event loop.

When each code is used in `request.create` (pinned in week 3):

| Situation | Code |
|---|---|
| The user has an unfinished session | `session_exists` |
| The user already has a `pending` request | `request_pending` |
| The host is not connected, has not enabled "available", is busy with a session, or is answering another request | `host_unavailable` |
| `host_device_id` does not correspond to a device | `not_found` |
| The requested device is one of the user's own | `bad_request` |
| Someone other than the addressed host tries to accept or reject | `forbidden` |

All times are ISO-8601 in UTC with a `Z` suffix. Identifiers are UUID strings.

## 3. Client-to-server messages

| Type | Fields | Notes |
|---|---|---|
| `hello` | `token` (the access JWT), `device_id`, `app_version`, `diagnostics` (an optional object: `firewall_rule_present`, `firewall_profile`, `vpn_adapter`, `vpn_holds_default_route`, `system_proxy_present`, `os_build`, `ipv6_global`) | The reply is `hello.ack`, or a `4401` close |
| `host.available` | `available` (bool), `listen_port` (int, optional when `true`) | Updates `presence.is_available_host`. With `listen_port` the server runs the reachability probe |
| `request.create` | `ref`, `host_device_id`, `duration_min` (1..max_session_minutes) | Replies `request.created {ref, request_id, expires_at}` and later `request.result` |
| `request.cancel` | `ref`, `request_id` | Only in the `pending` state |
| `request.accept` | `ref`, `request_id`, `auto` (bool, defaulting to `false`) | From the host only. `auto: true` means the host matched the request against a trusted-guest rule it had set in advance and so showed no window ([section 5a](#5a-auto-accepting-a-trusted-guest)) |
| `request.reject` | `ref`, `request_id` | From the host only |
| `session.endpoint` | `session_id`, `cert_fp_sha256` (lowercase hex, 64 characters), `candidates` (an array of `{type, ip, port}`; `type` is one of `lan`/`upnp`/`public`/`v6`) | From both sides after `session.created` |
| `session.connected` | `session_id`, `winner_type`, `connect_ms`, `tls_version` (`1.2`/`1.3`) | **From the host only**; moves the session to `active` |
| `session.connect_failed` | `session_id`, `diagnostics` (a free object: the candidates tried, and each one's timing and error) | From either side; the server ends with `connect_failed` |
| `session.stats` | `session_id`, `bytes_up`, `bytes_down` | From the host every 30 seconds |
| `session.end` | `session_id`, `reason` (`guest_ended`/`host_ended`), `bytes_up`, `bytes_down`, `domains` (an array of strings, possibly empty) | Accepted from either party to the session |
| `ping` / `pong` | no fields | |

## 4. Server-to-client messages

| Type | Fields | To whom |
|---|---|---|
| `hello.ack` | `server_time`, `public_ip` (as the server sees it), `settings` (`max_session_minutes`, `request_timeout_seconds`, `allowed_ports`, `log_domains`, `enforce_allowlist`), `allowlist_version` | The client, after `hello` |
| `hosts.snapshot` | `hosts`: an array of `{device_id, user_display_name, device_name, reachable (true/false/null)}` | After `hello.ack` |
| `hosts.update` | `hosts` (the complete new array) | Every client, on any change |
| `request.created` | `ref`, `request_id`, `expires_at` | The user |
| `request.incoming` | `request_id`, `guest_user_id`, `guest_device_id`, `guest_name`, `guest_device`, `duration_min`, `allowlist_version`, `expires_at` | The host |
| `request.result` | `request_id`, `accepted` (bool), `reason` (`rejected`/`expired`/`cancelled`/`host_unavailable` when `false`), `session_id` (when `true`) | The user |
| `request.expired` | `request_id` | The host: a general "close the request window" frame, sent on the timeout expiring, on the user cancelling, and on the user disconnecting |
| `session.created` | `session_id`, `role` (`guest`/`host`), `secret_b64` (32 bytes, Base64), `expires_at`, `allowlist_version`, `peer_public_ip`, `same_public_ip` (bool), `peer` (`{user_id, device_id, user_display_name, device_name}`), `relay` (`{address, port, token}` or `null`) | Both sides |
| `session.peer_endpoint` | `session_id`, `cert_fp_sha256`, `candidates` | The other side, for whoever sent `session.endpoint` |
| `session.active` | `session_id`, `expires_at` | Both sides |
| `session.terminate` | `session_id`, `reason` | Both sides (or the remaining one) |
| `allowlist.updated` | `version` | Every client |
| `error` | `ref` (optional), `code`, `message` | The client concerned |
| `ping` / `pong` | no fields | |

**The `relay` object in `session.created`** (added on 2026-09-07 by [ADR-0009](decisions/0009-relay-default.md)):
`{address, port, token}`, or `null` if no relay is configured in the deployment — a supported deployment meaning
"direct only", as it was before the decision.

- `token` is signed with HS256 and bound **to the session and the role together**, and is short-lived. Without binding
  the role, the holder of one token could open both ends of the relay and pair with themselves, seizing the session and
  depriving their peer of it.
- `token` is **a bearer statement** until it expires: it is written to no log, appears in no diagnostics, and is not
  stored on the server (the relay verifies the signature rather than looking the session up, so nothing needs to
  persist).
- Each side receives **its own token**; two different tokens in one session.

## 5. The session's lifecycle (the server is the authority on state)

```
request(pending, 60s)
  ├─ reject / cancel / timeout ──▶ request(rejected|cancelled|expired) + request.result(false)
  └─ accept ──▶ session(connecting) + session.created to both sides
session(connecting)
  ├─ both sides send session.endpoint → the server passes each to the other as session.peer_endpoint
  ├─ the host sends session.connected ──▶ session(active) + session.active to both sides
  ├─ either side sends session.connect_failed ──▶ ended(connect_failed)
  └─ a 30-second timeout from created ──▶ ended(connect_failed)
session(active)
  ├─ the expires_at timer ──▶ ended(expired)
  ├─ session.end from one side ──▶ ended(guest_ended|host_ended)
  ├─ a WS disconnection on one side ──▶ ended(guest_disconnected|host_disconnected)
  └─ an administrator terminating ──▶ ended(admin_terminated)
ended: delete session_keys, save the statistics and domains, send session.terminate to the side/sides, update presence
```

The end reasons: `guest_ended`, `host_ended`, `expired`, `guest_disconnected`, `host_disconnected`, `connect_failed`,
`admin_terminated`, `browser_not_proxied`, `protocol_error`.

Rules:
- One unfinished session per user and per device; the `request.create` failure codes are detailed in the table in
  section 2.
- A host with an unfinished session does not appear in `hosts.*` until it ends.
- `session.connected` from the user is ignored with `error(forbidden)`.

## 5a. Auto-accepting a trusted guest

The host can answer `request.incoming` **without a window being shown**, if the request matches a trusted-guest rule
the host had set themselves in advance. The consent here is real, not abolished: it was given once before the request
rather than at it. What changes is the moment it was given, not its existence.

**The whole decision is in the host's client.** The server stores no trust lists, approves on nobody's behalf, and
holds no opinion about the host's rules. If the host is disconnected from `/ws` they are outside `hosts.*` to begin
with and no request reaches them. This is deliberate: "automatic acceptance" means the host's application answers
instead of their person, not that the server answers instead of their machine.

**The rule's key is the pair `(guest_user_id, guest_device_id)`** from `request.incoming`. The two displayed names
(`guest_name`, `guest_device`) are chosen by the guest, changed whenever they like, and may repeat between users, so a
rule written on a name bequeaths a decision taken about one person to a stranger who renamed themselves. The device is
part of the key rather than the user alone: a guest who installs the application on a new machine gets a new
`device_id` and is asked about afresh, exactly as `known_hosts` does.

The host's client must not accept automatically unless **all** of the following hold; otherwise it shows the window as
usual:

| Condition | Why |
|---|---|
| The master auto-accept switch is on | It is off by default; switching it off stops every rule at once |
| The pair `(guest_user_id, guest_device_id)` is in the trust list | The rule was written about this guest on this device |
| The rule has not expired | Trust with no term outlives the reason for it |
| `duration_min` ≤ the rule's duration ceiling | The host trusted a short session, not a whole day |
| `enforce_allowlist` is off, or `allowlist_version` is the same one recorded when trust was granted | The list changed, so what the request grants is no longer what the host agreed to |

An automatic acceptance sends `request.accept` with `auto: true`. The server then records a `request_auto_accepted`
security event attributed to **the host** (the rule's owner), with the guest's identity and the duration as its
details, because this is the one consent nobody witnessed as it happened, so it is the consent most in need of being on
the record. Otherwise nothing differs: the session is created, run and ended by the same path as in section 5.

The host must **inform, not ask**: a non-blocking notification saying that so-and-so connected automatically, with an
immediate end to the session in it. An entirely silent acceptance makes the host discover their connection was used
from a bill or from slowness, and that is not what they agreed to.

## 6. The reachability probe

On `host.available {available:true, listen_port}`: the server makes a TCP connection to
`presence.public_ip:listen_port` with a 3-second timeout, updates `presence.reachable` and broadcasts `hosts.update`.
Not one byte is sent; the connection is closed the moment the TCP handshake succeeds. The technical prototype uses the
same method through `POST /api/v1/probe`.

## 7. Close codes

| Code | Meaning |
|---|---|
| `4401` | No valid `hello` arrived in time |
| `4403` | The device is revoked or the user is disabled |
| `4409` | A newer connection from the same device |
| `1012` | The server is restarting; reconnect with exponential backoff (1, 2, 4… up to 30 seconds) |
| `1001` | The heartbeat timed out (no `pong` within 40 seconds); transient, reconnect |
| `1011` | An unexpected failure during the handshake; transient, reconnect |

## 8. Details pinned in week 3

- **`hosts.update` is built per recipient** rather than as one shared frame, because the list excludes the recipient's
  own devices. `GET /hosts` excludes them too, so it matches `hosts.snapshot` exactly (a behaviour change from week 2).
- **The `ref` in `request.cancel`, `request.accept` and `request.reject`** has no positive reply; it is used only to
  correlate an `error` message.
- **`peer_public_ip`** is an empty string when it cannot be known, not `null`.
- **`hello.app_version`** is optional: a cosmetic field that may not stop a client connecting.
- **`hello.diagnostics`** is saved in `connect_diagnostics` with an empty `session_id`; anything over 64 KB is dropped
  with a warning and does not break the connection. The object is open: the keys listed are the expected ones, and any
  additional key is stored as it is.

`cgnat_suspected` was computed from the UPnP gateway's report alone, so when there is no gateway to ask — which is the
case on **a phone hotspot, where CGNAT actually lives** — it returned `false`: the same value that means "checked and
not present". Two keys were added in week 8 to separate the observation from the silence, on the model of
`vpn_adapter`/`vpn_holds_default_route`:

- **`cgnat_checked`**: whether a judgement was possible at all. When it is `false`, `cgnat_suspected` means **"we do
  not know"**, not "not present".
- **`nat_reachability`**: `open` (the machine holds the public address) · `mapped` (one NAT answered and opened the
  port) · `blocked` (direct will not work) · `unknown`. And this is usually answered **even when CGNAT cannot be
  judged**: if the server sees an address the machine does not hold, inbound is blocked whatever the shape of the NAT
  above it.
- **`nat_evidence`**: the observation the judgement rested on, in words, so the row can be read months later.

`cgnat_suspected` is now also raised with no gateway: **a local address inside `100.64.0.0/10`** is CGNAT spelled out.

`vpn_adapter` says a VPN adapter **exists**, and `vpn_holds_default_route` (added in week 6) says it **actually holds
the egress route** — and that alone is what predicts the sites seeing an unexpected address. The client warns the host
on the second, not the first, and separating them lets the server explain a session after the fact.
- **`X-Forwarded-For`** is honoured only if the peer is a private or loopback address (that is, our own proxy in
  front), so a direct client cannot forge `public_ip`, which is the target of the reachability probe.
- **The reachability probe** is only run against a public address; otherwise `reachable` stays `null` (unknown).
- **Requests settled by a disconnection** are stored with the state `cancelled` and a `responded_at`, while the reason
  on the wire is `host_unavailable` for the user and `request.expired` for the host.
- **Deregistering a device** closes its channel at once with code `4403` and clears its presence.
- **An `error` reply to `RequestAsync`:** the real control channel in the client raises an exception carrying the code,
  while the simulated channel returns an `error` message. The consumer handles both until the simulated one is
  withdrawn.

## 9. Details pinned in week 4 (the session messages)

**The error codes for the session messages**, in the order they are checked:

| Situation | Code |
|---|---|
| An unknown `session_id`, or the session has ended | `not_found` |
| The sender is not a party to the session | `forbidden` |
| The role does not permit the message (a user sending `session.connected` or `session.stats`) | `forbidden` |
| The session is alive but its state does not suit the message | `bad_request` |

**Field validation:**
- `candidates[].ip` is an IP address literal (v4 or v6), not a hostname, and is stored in its canonical form. The limit
  is 16 candidates, and a duplicate is a match on the `(type, ip, port)` triple; the same `ip:port` under two different
  types (`upnp` and `public`) is allowed.
- `winner_type`: `lan|upnp|public|v6|relay` — **widened on 2026-09-07 by
  [ADR-0009](decisions/0009-relay-default.md)**. Its vocabulary is deliberately wider than `candidates[].type`'s:
  `relay` is not a candidate to be connected to (it never appears in `session.endpoint`, and is refused there with
  `bad_request`), but it is a correct answer to "what carried this session", and without it the `admin/diagnostics`
  report is blind to the transport that actually works.
- `connect_ms` ≤ 2³¹−1, and the byte counts ≤ 2⁶³−1 (matching the column types); exceeding them is `bad_request`.
- `session.end.domains` is optional and treated as an empty list when absent: an ending is not refused over a secondary
  detail. `session.connect_failed.diagnostics` is mandatory.
- `session.endpoint` is accepted in the `connecting` state only; after `active` it is refused with `bad_request`.

**The exchange's behaviour:** the second party in the endpoint exchange receives its peer's endpoint twice (the first
pass, then the order-independent reply). The operation is idempotent at the client, and suppressing it reintroduces the
late-joiner bug.

**The timers:** both are scheduled on acceptance: the connect timeout (`connect_timeout_seconds`) and the `expires_at`
timer. `session.connected` re-arms the expiry timer with no double effect, so the timer also covers a session stuck in
`connecting`. Cancellation happens inside the single termination path, so it does not drift apart from deleting
`session_keys` and broadcasting `session.terminate`.

**`session.active.expires_at`** repeats the same value from `session.created`: activation does not move the deadline.

**The `session.end` reasons a client may send** (pinned after a conflict between the two tracks was found in week 4):

| Reason | Who sends it | Why |
|---|---|---|
| `guest_ended` / `host_ended` | The one named | The user stopped the session themselves |
| `guest_disconnected` / `host_disconnected` | **The peer**, not the one named | The tunnel died while both control channels stayed alive, and that is the one moment the server cannot observe itself |
| `browser_not_proxied` | Either side | The work browser did not go through the proxy |
| `protocol_error` | Either side | A protocol fault |

And `expired`, `connect_failed` and `admin_terminated` are **the server's judgements alone**: it has its timers, its
timeout and its administrator's order, and it answers `bad_request` to any client that claims them. So when the
duration runs out the client tears its session down locally at once with no `session.end`, and waits for the
`session.terminate` that issues within the difference between the two clocks, because both timers derive from the same
`expires_at`.

**The domain log:** the characters are lowercased, the trailing dot is removed, and entries that are empty, contain
spaces or control characters, or exceed 255 characters are ignored, with a maximum of 200 distinct domains per session.

**`connect_result`** takes `ok`, `failed` or `timeout`, and the last is counted among the failures in the
`GET /admin/diagnostics` summary.
