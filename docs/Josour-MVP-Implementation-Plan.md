# The Josour implementation plan — the first release (MVP)

## 1. Context

The project starts from nothing: the directory `/Users/moshaker/project` contains only `Josour.pdf` (the product
definition and release plan, 16 pages). What is asked for is a complete implementation plan for the first release as
the document defines it in sections 6 and 10 to 17.

**The first release's goal:** to prove the system can establish a secure and stable connection between the user's
machine (the guest) and the host's machine, and pass an independent work browser through the host's IP address, with a
simple interface, with no browsing data passing through the central server.

**The governing constraints from the document:**
- The backend server (FastAPI) is for control and coordination only. Browsing data passes directly between the two
  machines over an independent encrypted channel.
- A relay server is deferred, and is only added if the tests prove the direct connection fails. QUIC is deferred.
- No decrypting HTTPS, no content logging, and no reaching the local network, localhost or the host's internal
  addresses.
- One host hosts one user. One active session per user. A definite duration per session.
- The technologies: C# + .NET for the client; Python + FastAPI + PostgreSQL + native WebSockets for the server; a small
  Linux VPS with Docker Compose.

---

## 2. The technical decisions (what the document left open + what the technical review revealed)

| Decision | What was adopted | Why |
|---|---|---|
| The client's interface | **WPF on .NET 8** + `WPF-UI` for a modern look, `CommunityToolkit.Mvvm`, `H.NotifyIcon.Wpf` for the tray, `Microsoft.Toolkit.Uwp.Notifications` for notifications | WinUI 3 has no official tray, needs the Windows App SDK runtime, and its notification story outside MSIX is newer. A one-day prototype in stage 0 settles the decision as the document asked |
| The installer | **A signed, unpackaged installer (Inno Setup)**, no MSIX in the MVP | MSIX passes the package identity on to the launched browser and changes the profile's path. The elevated installer adds a Windows Firewall rule for the application |
| The TLS version | `SslProtocols.None` (the system default) with **a refusal of any negotiation below TLS 1.2** and the negotiated version recorded | Schannel on Windows 10 does not support TLS 1.3, and asking for it explicitly throws. TLS 1.3 is used automatically on Windows 11. **This is an amendment to the document's text (10 and 13) and needs the product owner's approval** |
| Establishing the connection | **A symmetric connection**: both sides open a listener, gather candidates and connect to the other's candidates in parallel, and the first connection authenticated at the host becomes the tunnel | It doubles the chance that one of the two sides is reachable (a host at the office and a user at home, say) with the same code plus a role flag |
| NAT traversal | Candidates: a public IP (as the server sees it) + UPnP/NAT-PMP through **`Mono.Nat` 3.x** + public IPv6 + LAN addresses only if both sides' public IP matches. **No TCP hole punching in the MVP**. A relay decision gate after the prototype | Open.NAT is abandoned. Hole punching costs a week and fails precisely on corporate networks. A simple relay (passing opaque bytes) is built in two days if the gate fails |
| Multiplexing | A two-day prototype of `Nerdbank.Streams.MultiplexingStream` first; otherwise hand-written framing (the specification in 7.3) | A mature library with built-in backpressure saves a week's work if it succeeds |
| The local proxy | HTTP CONNECT only, on `127.0.0.1:0` (a port the system assigns), **accepting connections from the launched browser's processes alone** (the owning-PID check) | It stops any other program on the user's machine exploiting the tunnel (requirement 6.5: this browser's connections only) |
| The site-list decision | **Both sides**: the user decides the routing (tunnel or direct), and the host decides the authorisation and refuses what is not in the list | The host is responsible for their own IP and their network; we do not trust the user's claim |
| Sites that are not allowed | They pass over the user's ordinary connection (reading the document, 6.6: "passing the rest of the sites over the user's ordinary connection") | "Blocking domains that are not allowed" is understood as stopping them passing through the host. A "block instead of pass" option is added in the second release |
| The server's workers | One uvicorn worker | The WS connection registry is in memory. Redis Pub/Sub is the upgrade path |
| The admin panel | **User management inside the application** (for anyone whose role is `admin`) + the CLI (`manage.py`) + the interactive OpenAPI | The document deferred a web interface to the second release; the in-application panel was added in its place, so no administrative surface is exposed on the internet |
| Device registration | The device is registered at its first sign-in with an id and a secret stored with DPAPI, and it is sent with every sign-in. An administrator can revoke it, blocking it | It satisfies "stopping use on unauthorised devices" |

**A documented trust boundary (ADR):** the server knows the session secret and the certificate fingerprint, so a
compromised server that controls the path could in theory intercept the tunnel. Accepted in the MVP. The hardening in
the second release: a long-lived key pair per device signing the session certificate's fingerprint.

**Approved by the product owner on 2026-09-03:** TLS 1.3 where available with a floor of 1.2 (an amendment to the
document's text, documented in an ADR), a relay decision gate after the technical prototype, passing non-allowed sites
over the user's ordinary connection, and implementation by a team of more than two developers (section 12 is built on
3 developers + a DevOps/QA track).

---

## 3. The repository's layout (a monorepo)

```
josour/
├── backend/                          # Python 3.12 + FastAPI
│   ├── app/
│   │   ├── main.py                   # creating the app, the routers, lifespan
│   │   ├── core/                     # config.py, security.py (argon2id + JWT), logging.py
│   │   ├── db/                       # session.py (async engine), base.py
│   │   ├── models/                   # SQLAlchemy 2.0 (section 4)
│   │   ├── schemas/                  # Pydantic v2
│   │   ├── api/routers/              # auth, me, devices, hosts, sessions, domains, admin_*
│   │   ├── ws/                       # router.py, connection_manager.py, protocol.py, handlers.py
│   │   ├── services/                 # presence, request, session, session_timer, reachability_probe, stats, audit
│   │   └── cli.py                    # Typer: create-admin, create-user, add-domain, list-sessions, end-session
│   ├── alembic/
│   ├── tests/
│   ├── Dockerfile
│   └── pyproject.toml
├── client/                           # a .NET 8 solution
│   ├── Josour.sln
│   ├── src/
│   │   ├── Josour.Core/         # the models, the session state machine, AllowlistMatcher, IpRangePolicy, the protocol messages (no dependencies)
│   │   ├── Josour.Tunnel/       # TlsTunnelFactory, AuthHandshake, CandidateGatherer, CandidateDialer, TunnelListener, Mux
│   │   ├── Josour.Proxy/        # ConnectProxyServer (the user's side), ProbePage, OwnerPidChecker
│   │   ├── Josour.Egress/       # EgressPolicy, OpenHandler (the host's side), ByteCounter, DomainCollector
│   │   ├── Josour.Browser/      # BrowserLocator, PolicyDetector, BrowserLauncher (a Job Object), GracefulCloser
│   │   ├── Josour.Infrastructure/ # ApiClient, ControlChannel (WS), DpapiSecretStore, DeviceInfo, Serilog
│   │   └── Josour.App/          # WPF + MVVM + tray + toasts + DI (the Generic Host)
│   ├── tests/                        # Josour.Core.Tests, Tunnel.Tests, Proxy.Tests, Egress.Tests, Browser.Tests
│   ├── tools/Josour.Spike/      # console tools for the technical prototype (stage 0)
│   └── installer/                    # Inno Setup + the firewall rule + the signing script
├── deploy/                           # docker-compose.yml, Caddyfile, backup/, .env.example
├── docs/                             # protocol.md, ws-protocol.md, api.md, runbook.md, acceptance-checklist.md, decisions/ (ADRs)
├── .github/workflows/                # ci-backend.yml, ci-client.yml
└── README.md
```

---

## 4. The data model (PostgreSQL)

| Table | The main columns | Notes |
|---|---|---|
| `users` | id, email (unique), password_hash (argon2id), display_name, role (admin/user), is_active, failed_logins, locked_until, created_at | Created by an administrator only |
| `devices` | id, user_id, name, os_version, os_build, device_secret_hash, status (active/revoked), last_seen_at, created_at | The secret is generated on the machine and stored with DPAPI |
| `refresh_tokens` | id, user_id, device_id, token_hash, expires_at, revoked_at | Rotation on every refresh |
| `presence` | device_id (pk), connected, is_available_host, public_ip, reachable (bool/null), updated_at | Cleared when the server starts |
| `connection_requests` | id, guest_user_id, guest_device_id, host_user_id, host_device_id, requested_minutes, status (pending/accepted/rejected/expired/cancelled), created_at, responded_at, expires_at | Expires after 60 seconds with no answer |
| `sessions` | id, request_id, guest_*, host_*, status (connecting/active/ended), created_at, started_at, expires_at, ended_at, end_reason, bytes_up, bytes_down, connect_result, winner_type, tls_version, connect_ms | end_reason: guest_ended / host_ended / expired / guest_disconnected / host_disconnected / connect_failed / admin_terminated / browser_not_proxied / protocol_error |
| `session_keys` | session_id (pk), secret, guest_cert_fp, host_cert_fp, guest_candidates (jsonb), host_candidates (jsonb), created_at | **The row is deleted the moment the session ends** |
| `allowed_domains` | id, entry, is_active, note, updated_at | `entry` in the form `example.com` (including subdomains) or `=exact.com` or `host:port`. An `allowlist_versions` table carries the version number |
| `session_domains` | id, session_id, domain, hit_count | Filled at the session's end if the `log_domains` setting is on |
| `connect_diagnostics` | id, session_id, device_id, role, data (jsonb), created_at | The prototype's and the relay gate's data: firewall_profile, firewall_rule_present, upnp_found, mapping_ok, upnp_external_ip, cgnat_suspected, ipv6_global, system_proxy_present, vpn_adapter, candidates_tried, per-candidate latency/error |
| `security_events` | id, type, user_id, device_id, ip, details (jsonb), created_at | Sign-in attempts, the lockout, revocations, central termination, unauthenticated connections on the listener |
| `app_settings` | key (pk), value | max_session_minutes, request_timeout_seconds, log_domains, allowed_ports |

**Constraints:** a unique partial index guarantees one unfinished session per user and per device.

---

## 5. The REST interfaces (`/api/v1`)

**Public (Bearer JWT):**
- `POST /auth/login` (email, password, device: {id?, secret?, name, os}) → an access token (15 minutes) + a refresh
  token (30 days) + device_id (+ device_secret at first registration). A revoked device is refused.
- `POST /auth/refresh`, `POST /auth/logout`
- `GET /me`, `GET /me/devices`, `DELETE /me/devices/{id}`
- `GET /hosts` — the available hosts with a reachability badge (from the server's probe)
- `GET /sessions/me`
- `GET /domains` → `{version, entries[]}` with an ETag. Supports `?version=` to fetch a specific version

**Administration (role=admin):**
- `POST|GET /admin/users`, `PATCH /admin/users/{id}` (enable/disable/password/unlock)
- `GET /admin/devices`, `POST /admin/devices/{id}/revoke`
- `GET /admin/domains`, `PUT /admin/domains` (a full replacement → a new version → broadcasting `allowlist.updated`)
- `GET /admin/sessions`, `POST /admin/sessions/{id}/terminate`
- `GET /admin/security-events`, `GET /admin/diagnostics` (the relay gate's summary)
- `GET|PATCH /admin/settings`

**Protection:** a rate limit on `/auth/login` (5/minute/IP), an account lockout of 15 minutes after 10 failed attempts,
and every attempt recorded.

---

## 6. The WebSocket protocol (`/ws`, authenticated by a `hello` message carrying the access token)

| Direction | Type | Contents |
|---|---|---|
| client→server | `hello` | token, device_id, app_version, diagnostics (firewall_rule_present, vpn_adapter, system_proxy…) |
| client→server | `host.available` | available, listen_port (for the server's reverse probe) |
| client→server | `request.create` / `request.cancel` | host_device_id, duration_min / request_id |
| client→server | `request.accept` / `request.reject` | request_id |
| client→server | `session.endpoint` | session_id, cert_fp_sha256, candidates: [{type: lan/upnp/public/v6, ip, port}] **(from both sides)** |
| client→server | `session.connected` | session_id, winner_type, connect_ms, tls_version **(from the host only, the authority)** |
| client→server | `session.connect_failed` | session_id, diagnostics |
| client→server | `session.stats` | session_id, bytes_up, bytes_down (every 30 seconds from the host) |
| client→server | `session.end` | session_id, reason, bytes_up, bytes_down, domains[] |
| server→client | `hello.ack` | server_time, public_ip, settings, allowlist_version |
| server→client | `hosts.snapshot` / `hosts.update` | The available hosts: the user's name, the device's name, the state, reachable |
| server→client | `request.incoming` | request_id, guest_name, guest_device, duration_min, allowlist_version, expires_at (to the host) |
| server→client | `request.result` | request_id, accepted, session_id (to the user) |
| server→client | `session.created` | session_id, role, secret_b64, expires_at, allowlist_version, peer_public_ip, same_public_ip **(to both sides)** |
| server→client | `session.peer_endpoint` | cert_fp_sha256, candidates[] (each side receives the other's candidates) |
| server→client | `session.active` | session_id, expires_at |
| server→client | `session.terminate` | session_id, reason (central termination or the other side disconnecting) |
| server→client | `allowlist.updated` | version |
| both | `ping` / `pong` | A heartbeat every 20 seconds; losing two beats = a disconnection |

**The session's lifecycle (the server is the authority on state):**
```
request(pending, 60s) ─accept─▶ session(connecting): created → endpoints from both sides → peer_endpoint
   connecting ─host: connected─▶ active            | a 30-second timeout ─▶ ended(connect_failed)
   active ─the expires_at timer─▶ ended(expired)
   active ─a WS disconnection on one side─▶ ended(*_disconnected) + terminate to the other
   active ─session.end from one side─▶ ended(guest_ended|host_ended) + terminate to the other
   active ─admin─▶ ended(admin_terminated) + terminate to both
```
On `ended`: deleting `session_keys`, saving the statistics and the domains, updating `presence`.

**The server's reachability probe:** on `host.available` with a listening port, the server tries a short TCP connection
to the host's public IP:port and updates `presence.reachable`. It appears as a badge in the host list and feeds the
relay gate without waiting for sessions.

---

## 7. The channel protocol between the two machines (`docs/protocol.md`)

### 7.1 Establishing the connection (symmetric)
1. On `session.created` each side: generates a self-signed certificate (ECDSA P-256, EKU serverAuth+clientAuth, valid
   until the session's end + an hour, re-imported from PFX with the `UserKeySet` flag so it works with Schannel),
   opens a listener on `IPv6Any` in DualMode on port 0, requests a UPnP/NAT-PMP mapping (Mono.Nat, the mapping's
   lifetime = the session's duration + 5 minutes, warmed up in advance when "available" is enabled), gathers the
   candidates (LAN only when `same_public_ip`), and sends `session.endpoint`.
2. On `session.peer_endpoint`: each side connects to all of the other's candidates in parallel (a 5-second timeout per
   candidate).
3. For every successful TCP connection: a TLS handshake (10 seconds) — the connector pins the other side's certificate
   fingerprint and ignores chain errors, `TargetHost` is fixed at `josour`, with no revocation check — then
   authentication inside TLS (5 seconds). **The user always speaks first**, whoever connected.
4. The host keeps the first connection that authenticates, closes the rest, and sends `session.connected`. Both sides
   close the listener, remove the UPnP mapping and cancel the remaining attempts.
5. If nothing authenticates within 30 seconds: `session.connect_failed` with the diagnostics.

**The listener only lives in the connect window:** a limit of 4 pending unauthenticated connections, the host is silent
until authentication succeeds, and any stranger's connection is closed and recorded.

### 7.2 Authentication inside TLS
- `AUTH1` (user→host): `v=1 ‖ session_id(16B) ‖ client_random(32B) ‖ HMAC-SHA256(secret, "rb-auth1" ‖ session_id ‖ client_random ‖ listener_cert_fp)`
- `AUTH2` (host→user): `HMAC-SHA256(secret, "rb-auth2" ‖ session_id ‖ client_random ‖ listener_cert_fp)`
- A constant-time comparison. After success the mux starts. After negotiation any version below TLS 1.2 is refused and
  the version is recorded.
- At the end: an explicit `Dispose` of the certificates (which deletes the key container from Windows) and wiping
  `secret` from memory.

### 7.3 The frames and flow control (if Nerdbank is not adopted)
```
An 8-byte header: [u8 type][u8 flags][u16 len][u32 stream_id]   the payload ≤ 16 KiB
```
The types: `OPEN` ([u16 port][u8 hostlen][host]) / `OPEN_OK` / `OPEN_FAIL` (u8 reason: not_allowed, private_ip,
port_not_allowed, dns_failed, connect_failed, limit) / `DATA` / `WINDOW_UPDATE` (u32) / `CLOSE` (a half-close =
Shutdown(Send) at the other end) / `RST` / `PING`/`PONG` (8 bytes) / `GOAWAY`.
- Stream ids are odd, increasing, and never reused. Only the user opens streams.
- A per-stream receiving window of **1 MiB** (with no connection-level window). Credit is returned **after the bytes
  are written to the destination socket**, and `WINDOW_UPDATE` is sent when 25% of the window has been consumed.
- One writer drains a bounded `Channel<Frame>` (32), and every stream puts one frame (16 KiB) in at a time; that gives
  round-robin fairness automatically.
- The host's limits: 256 concurrent streams, 50 opens/second. The tunnel's liveness: `PING` every 20 seconds, dead
  after 60.

### 7.4 The host's side on `OPEN` (`EgressPolicy`)
1. Refuse any IP literal (it cannot be listed in the first place). Normalise the name: lowercase, strip the trailing
   dot, match on Punycode.
2. Match against the site list: `example.com` includes subdomains, `=exact.com` is exact only, `host:port` is
   optional. The port must be within `allowed_ports` (80, 443).
3. Resolve DNS **once** with `Dns.GetHostAddressesAsync`. If **any** resulting address is in the blocklist →
   `OPEN_FAIL(private_ip)`. Connect with `Socket.ConnectAsync(IPAddress[], port)` using the checked list **and not the
   name** (which closes DNS rebinding and Happy Eyeballs).
4. The blocklist: `0/8, 10/8, 100.64/10, 127/8, 169.254/16, 172.16/12, 192.0.0/24, 192.0.2/24, 192.168/16, 198.18/15,
   198.51.100/24, 203.0.113/24, 224/4, 240/4, 255.255.255.255`, and IPv6: `::, ::1, ::ffff:0:0/96 (unwrapped, with the
   v4 checked), 64:ff9b::/96, 2002::/16, 2001::/32 (Teredo), fc00::/7, fe80::/10, ff00::/8`, **plus every address of
   the host's interfaces, its default gateways and the public IP the server sees** (which stops the router's admin page
   being reached through the public IP).
5. Connect with a 10-second timeout, `OPEN_OK`, bidirectional pumping with a byte counter and collection of the
   distinct domains.

### 7.5 The user's side (`ConnectProxyServer`)
- It listens on `127.0.0.1:0` and reads the assigned port before building the browser's command line.
- **The owner check:** for every inbound connection, `GetExtendedTcpTable` is queried for the owning PID; it is
  accepted only if it is inside the browser's Job Object.
- `CONNECT host:port` (including `ws://`, `wss://` and the `[IPv6]:port` form): allowed → `OPEN` through the tunnel and
  **`200 Connection Established` only after `OPEN_OK`** (otherwise a truthful 403/502/504); not allowed → a direct TCP
  connection from the user's machine (respecting the system proxy if there is one, by sending CONNECT to it).
  `OPEN_FAIL(not_allowed)` during a list-divergence window → falling back to direct.
- Ordinary `http://` requests: for an allowed domain → a local `307` reply to `https://` (no plaintext bytes through
  the tunnel). For a non-allowed one → one request per connection: converting the absolute-URI to origin-form, removing
  `Proxy-Connection`, adding `Connection: close`, and pumping until the origin closes. (Chromium reuses the proxy
  connection for different origins, so blind pumping mixes destinations.)
- **The check page:** the browser opens on `http://check.josour/`; the proxy intercepts it and replies with a page
  saying "The tunnel is active. The sites will see: <the host's public IP>". If that request does not arrive within 10
  seconds the browser is not using the proxy → the session ends with `browser_not_proxied`.
- Any CONNECT to a private address or localhost is refused locally too (defence in depth).

### 7.6 The cleanup order at the end
1. The proxy stops accepting new CONNECTs. 2. A polite browser close, then killing the job. 3. `GOAWAY` and closing the
tunnel. 4. Closing the listener and removing the UPnP mapping. 5. Disposing the certificates and wiping the secret.
6. `session.end` with the statistics and the domains.

### 7.7 The relay decision gate
After the stage-0 prototype on at least 10 real pairs from the target users' networks over two weeks: if the rate of
connecting within 10 seconds is below 85%, stage 7 (the relay) is implemented. The prior estimate for the audience the
document names (companies and distributed teams) is about 45% to 65% with a symmetric connection, so the probability of
needing the relay is high; so the transport is built behind an `ITunnelTransport` interface from the start, making
`RelayTransport` an addition rather than a change.

---

## 8. The Windows application (WPF + .NET 8)

### 8.1 The components
| Project | Components |
|---|---|
| `Core` | `SessionStateMachine`, `Role`, `AllowlistMatcher` (a pure function shared by both sides), `IpRangePolicy`, the WS message models, the `IControlChannel`, `ITunnelTransport`, `IBrowserSession`, `ISecretStore` interfaces |
| `Tunnel` | `TlsTunnelFactory` (generating the certificate, pinning, `SslProtocols.None` + a ≥1.2 check), `AuthHandshake` (AUTH1/AUTH2), `CandidateGatherer` (interfaces, Mono.Nat, IPv6, the public IP), `TunnelListener`, `CandidateDialer` (parallel, first winner), `MuxAdapter` (Nerdbank or `FrameCodec`+`MuxConnection`+`MuxStream`+`AsyncCredit`) |
| `Proxy` | `ConnectProxyServer`, `HttpRequestParser`, `ProbePage`, `OwnerPidChecker` (P/Invoke to iphlpapi) |
| `Egress` | `EgressPolicy`, `OpenHandler`, `SafeConnector`, `ByteCounter`, `DomainCollector`, `StreamLimiter` |
| `Browser` | `BrowserLocator` (the registry's App Paths under HKLM/HKCU/WOW6432Node, then the default paths, and verifying a Google/Microsoft signature), `PolicyDetector` (the `SOFTWARE\Policies\Google\Chrome` and `\Microsoft\Edge` keys: ProxySettings/ProxyMode/ProxyServer/UserDataDir; it prefers the unmanaged browser), `BrowserLauncher` (a Job Object with `KILL_ON_JOB_CLOSE`, handoff detection if the process exits within 3 seconds, setting `exit_type=Normal` in Preferences), `GracefulCloser` (WM_CLOSE to the job's windows, a 3-second wait, then closing the job) |
| `Infrastructure` | `ApiClient` (transparent token refresh), `ControlChannel` (`ClientWebSocket` + reconnection with exponential backoff + a heartbeat + re-announcing "available" + respecting the system proxy), `DpapiSecretStore` (the tokens and the device secret), `DeviceInfoProvider`, `FirewallRuleChecker`, `VpnAdapterDetector`, Serilog to `%LOCALAPPDATA%\Josour\logs` (with no frame payloads at all) |
| `App` | The Generic Host + DI, `LoginWindow`, `MainWindow` (the host and user tabs), `IncomingRequestWindow` (top-most + a sound, because Focus Assist silences notifications), `SessionPanel`, `SettingsView`, `TrayIcon`, a toast with accept/reject buttons, a single instance through a mutex, optional start with Windows (`HKCU\...\Run`) |

### 8.2 The browser's command line (Chrome and Edge alike)
```
--user-data-dir="%LocalAppData%\Josour\BrowserProfile"
--proxy-server="http://127.0.0.1:<port>"
--no-first-run --no-default-browser-check --disable-sync
--disable-background-networking --disable-component-update
--disable-quic
--force-webrtc-ip-handling-policy=disable_non_proxied_udp
--hide-crash-restore-bubble
--new-window "http://check.josour/"
```
- **No** `--proxy-bypass-list`: the default bypasses loopback only, which is what we want; the check page's name is
  deliberately not loopback so that it goes through the proxy.
- The system proxy is not changed, so there is nothing to restore; "returning the browser to normal" is achieved by
  closing the separate profile and setting `exit_type`.
- DNS for the allowed sites is resolved at the host (Chromium sends the name in CONNECT and does not resolve it
  locally). WebRTC and QUIC are disabled in the work browser only.

### 8.3 The host's flow
1. Sign in → register the device → WS → `hello.ack` (carrying the public IP) and `hosts.snapshot`.
2. Enable "available" → check the firewall rule and the VPN adapter (warning if found) → warm up UPnP →
   `host.available` with the probe port.
3. `request.incoming` → a toast + a top-most window showing: the user's name, their device's name, the duration, the
   allowed sites (the current version), the warning "the sites will see your IP address", and that disconnecting is
   possible at any time (requirement 15) → accept/reject within 60 seconds.
4. `session.created` → establishing the symmetric connection (7.1) → `session.connected`.
5. `SessionPanel`: a **local monotonic** countdown from the moment of receipt (which works even if WS drops), the
   approximate data, and a "disconnect" button. If WS is down for more than 60 seconds the host ends the session
   itself (otherwise central termination cannot be carried out).
6. Termination for any reason → the cleanup (7.6) → back to "available".

### 8.4 The user's flow
1. Sign in → the list of available hosts with a reachability badge.
2. Choosing a host and a duration (15/30/60/120 minutes, the maximum from the server's settings) → `request.create` →
   a waiting screen with a cancel.
3. Rejected → a message. Accepted → `session.created` → the symmetric connection → starting the proxy → launching the
   browser on the check page.
4. `SessionPanel`: the time remaining, the state, a "launch the work browser" button (if it was closed by hand), and an
   "end" button.
5. Termination → the cleanup (7.6).

### 8.5 Edge cases
- Closing the application during a session → `session.end` and a full cleanup before exiting. The application crashing
  → the Job Object kills the browser automatically (fail-closed), and the server ends the session on the WS
  disconnection.
- The machine sleeping / a network change → the WS disconnection ends the session (reconnection after a short drop: the
  second release).
- The access token expiring → a transparent refresh and reopening WS with "available" re-announced.
- Time: `expires_at` comes from the server, and the client computes the remainder relative to `server_time` with a
  monotonic counter.
- A user on a corporate network with a mandatory proxy: WS respects `ClientWebSocketOptions.Proxy`, and the direct path
  passes CONNECT to the system proxy. `system_proxy_present` is recorded in the diagnostics.

---

## 9. The backend server (FastAPI)

- **The stack:** Python 3.12, FastAPI, Uvicorn, SQLAlchemy 2.0 async + asyncpg, Alembic, Pydantic v2 +
  pydantic-settings, `argon2-cffi`, `PyJWT`, `slowapi`, Typer, pytest + pytest-asyncio + httpx + websockets.
- **The connections:** an in-memory `ConnectionManager` (device_id → one connection; a new one closes the old).
  Broadcasting `hosts.update` when availability or reachability changes.
- **The timers:** `SessionTimer` with asyncio tasks: the request expiring (60 seconds), the connect timeout (30
  seconds), `expires_at`. At startup: ending the pending sessions and clearing `presence`.
- **Security:** HTTPS/WSS through Caddy, a short JWT, refresh rotation, argon2id, rate limiting, the account lockout,
  recording the attempts, revoking a device, central termination, and no storage of any browsing content. `/docs` is
  restricted to administrators in production.

---

## 10. Deployment (a small Linux VPS: 1 vCPU / 2 GB)

| Service | Image | Role |
|---|---|---|
| `caddy` | caddy:2 | Automatic TLS, a reverse proxy for HTTP and WebSocket, HSTS |
| `api` | built from `backend/Dockerfile` | One worker, `alembic upgrade head` at startup |
| `db` | postgres:16 | A persistent volume, the internal network only |
| `backup` | postgres:16 + cron | A daily `pg_dump` to `/backups`, 14 days of retention, a documented restore script |

`docs/runbook.md`: preparing Ubuntu (ufw with 80/443 only, fail2ban, automatic updates), deployment, upgrading,
restoring, the logs. (If a relay is added later it listens on 443 with TLS behind Caddy under its own SNI.)

---

## 11. The testing strategy

| Level | What is tested | Tool |
|---|---|---|
| Server units | The state machine, the request transitions, "one session", the roles, hashing, JWT, the list's versions | pytest |
| Server integration | The full REST surface against PostgreSQL in Docker; a complete WS scenario with two fake clients: request → accept → created → endpoints → connected → active → end/expired/disconnect/terminate; the reachability probe | pytest + httpx + websockets |
| Client units | `AllowlistMatcher` (a table of cases + Punycode), `IpRangePolicy` (an IPv4/IPv6 table with the wrapping), `FrameCodec` (corrupt/truncated/fuzzed frames), the mux over `Pipe` pairs: a slow consumer does not stall another stream, credit never goes negative, the half-close, `RST` releasing resources, 1000 sequential and 256 concurrent, 100 MB over local TLS; `AuthHandshake` (a wrong signature, a different fingerprint, a replay); `HttpRequestParser` | xUnit |
| Client integration | `ConnectProxyServer` + `OpenHandler` in the same process: an allowed CONNECT goes through the tunnel, a non-allowed one goes direct, a local/private one is refused by both sides, an allowed `http://` returns 307, the check page, refusing a connection from a stranger's PID | xUnit |
| Manual E2E | Two Windows machines (10 and 11) on two networks; `docs/acceptance-checklist.md` covers the 18 success criteria: ifconfig.me shows the host's IP in the work browser and the user's IP in their ordinary browser, Teams/Outlook unaffected, disconnecting from both sides, the automatic end, the server's inability to read browsing data and its not passing through the API container (`docker stats` on `api` and `relay` during a video) | Manual + Wireshark |
| Security | nmap on the listener sees TLS that closes without authentication; CONNECT to 192.168.x, localhost, [::1] and the host's public IP is refused; `session_keys` is empty after the end; `certutil -user -key` shows no accumulated key containers; the logs have no URLs or content; a browser with a corporate proxy policy ends the session with `browser_not_proxied` | Manual + automated |

---

## 12. The implementation plan in parallel tracks (3 developers + a DevOps/QA track, 9 calendar weeks)

### 12.1 The tracks and their owners
| Track | Owner | Scope |
|---|---|---|
| **A — the server** | A Python developer | The whole of `backend/`: the models, REST, WS, the state machine, the reachability probe, the diagnostics, the CLI, the pytest tests, and the relay if the gate opens |
| **B — networking** | A .NET developer (the strongest at networking) | `Core` (Matcher, IpRangePolicy, the state machine), `Tunnel`, `Egress`, `Proxy`, `Browser`, the `Spike` tool, the xUnit tests for all the layers |
| **C — the Windows application** | A .NET developer (interfaces) | `Infrastructure` (ApiClient, ControlChannel, DPAPI, Serilog), the whole of `App` (WPF, MVVM, tray, toast, the screens), and the final wiring between tracks B and A |
| **D — DevOps/QA** | A fourth developer, or a part-time role the team shares | The repository, CI, the signing certificate, the installer, the VPS/Caddy/backups, the runbook, the acceptance list, the security test scripts, running E2E across the matrix |

### 12.2 The contracts frozen first (a condition for parallelism)
Written and frozen by the end of **week 1** and reviewed by all four tracks together:
- `docs/ws-protocol.md` (section 6) and `docs/api.md` (section 5) — A builds the server and C builds the client on
  them, and C works against a `MockControlChannel` until A is ready.
- `docs/protocol.md` (section 7) — B builds on it, and C consumes it through the `Core` interfaces.
- The `Josour.Core` interfaces: `IControlChannel`, `ITunnelTransport`, `ITunnelSession`, `IBrowserSession`,
  `ISecretStore` + `SessionStateMachine` — published first so C can build the screens on `FakeTunnelSession` and
  `FakeBrowserSession`.
- Any later change to a contract goes through review by both affected sides, and the document is updated before the
  code.

### 12.3 The weekly schedule

| Week | A — the server | B — networking | C — the application | D — DevOps/QA |
|---|---|---|---|---|
| **1** | The `backend/` layout, the settings, the models and migrations, argon2id + JWT, `auth`/`me`/`devices`; a minimal reachability endpoint to serve B's prototype | An `SslStream` prototype with temporary certificates on Win10/11 (TLS 1.2/1.3, deleting the keys); Mono.Nat on two or three routers; a console tool for the symmetric connection; publishing the `Core` interfaces | A WPF prototype (tray, a toast with buttons, a single instance, start on login) → the interface ADR; the `App` layout + DI + Serilog | The repository and its layout, CI for the server and the client, **requesting the code-signing certificate**, a staging VPS with an initial Compose |
| **2** | `domains` with versions, `admin_*`, rate limiting and the account lockout, the CLI, the Dockerfile, the integration tests on PostgreSQL | The Nerdbank prototype against hand-written framing → an ADR; an initial proxy with the check page; the browser launch matrix (Chrome/Edge × managed/unmanaged × Win10/11); **running 10 real pairs and gathering the diagnostics** | `ApiClient` + `DpapiSecretStore` + `DeviceInfoProvider`; the sign-in screen against A's server on staging; `MockControlChannel` from `ws-protocol.md` | The firewall rule through an initial Inno Setup installer; deploying A's server to staging; a draft `acceptance-checklist.md` |
| | **Milestone M0:** the contracts frozen, 4 ADR decisions (UI, mux, TLS, relay), **the relay gate decision** on real data, sign-in working against staging | | | |
| **3** | The WS router + `ConnectionManager` + the heartbeat + `hello`; presence and broadcasting hosts; the requests (creation/answer/cancellation/expiry) | `Core`: `AllowlistMatcher`, `IpRangePolicy`, `SessionStateMachine` with full tests; `Tunnel`: `TlsTunnelFactory`, `AuthHandshake` | The real `ControlChannel` (reconnection, heartbeat, the system proxy) against A; the main screen and the host list | The security test scripts (nmap, CONNECT to private addresses, checking `session_keys`), the test device matrix |
| **4** | The full session state machine: `created`/`endpoint`/`peer_endpoint`/`connected`/`active`/`terminate`, the timers, the disconnection, deleting `session_keys`, `connect_diagnostics`, the reachability probe | `Tunnel`: `CandidateGatherer`, `TunnelListener`, `CandidateDialer`, `MuxAdapter` with the Pipe tests; starting `Egress` | The incoming-request window with the full disclosure + a toast + top-most; the waiting screen; `SessionPanel` on `FakeTunnelSession` | Staging updated automatically from CI; backup and restore documented and exercised |
| | **Milestone M1:** an automated WS test with two fake clients passes end to end on A; the `Core`/`Tunnel` units are green; the application lists the hosts, sends a request and receives it on staging | | | |
| **5** | The statistics and the domains, `allowlist.updated`, `admin/sessions/terminate`, `admin/diagnostics`; hardening and cleanup; **if the relay gate opens: building the relay service (asyncio, a signed token, pairing two sockets)** | `Egress`: `EgressPolicy`, `OpenHandler`, `SafeConnector`, the limits, the counters; `Proxy`: CONNECT, the http 307, request/connection, `OwnerPidChecker` | The settings, a complete tray, the monotonic counter, the WS-disconnection rule for the host, closing the application during a session; wiring `SessionStateMachine` to the real `IControlChannel` | Running the runbook in full on a clean VPS; the SmartScreen check on the signed installer |
| **6** | Relay support in the protocol if needed (`session.relay` with a token); a security review of section 14 from the server's side; WS load (hundreds of connections) | `Browser`: `BrowserLocator`, `PolicyDetector`, `BrowserLauncher` with a Job Object, `GracefulCloser`; `RelayTransport` if needed; **a console tool proving `curl --proxy` between two machines with the host's IP, and launching Chrome on the check page** | Wiring B's real libraries into the application (Tunnel/Proxy/Egress/Browser) instead of the fakes; the cleanup order (7.6) | The automated security tests running in CI against staging; preparing two test machines (Win10 and Win11) on two networks |
| | **Milestone M2:** the first complete real session between two machines through the application: request ← accept ← connect ← the check page showing the host's IP ← disconnect | | | |
| **7** | Integration fixes; tuning the timeouts; the final OpenAPI documentation | Integration fixes; performance (heavy pages, video, 256 streams, an international RTT); the VPN warning | All the edge cases (8.5); polishing the interface and the messages; a "relaunch the browser" button | **E2E across the matrix** (Win10/11 × Chrome/Edge × home/office/phone networks); recording and ranking the defects |
| **8** | Reviewing section 14 item by item (jointly); fixes | The tunnel's security review (jointly); fuzzing the frames; fixes | Fixes; the final user experience | Running the full 18-item acceptance list on staging; `certutil`, Wireshark and `docker stats` on `api` and `relay` |
| | **Milestone M3:** the 18-item acceptance list green on staging and every automated test green | | | |
| **9** | Deploying production, `alembic upgrade`, the first administrator, the first users | Supporting the first run | Supporting the first run | The final signed installer, the production VPS, Caddy, the backups, the final runbook, **running the acceptance list on production** |
| | **Milestone M4 (the end of the first release):** a signed installer + production running + documentation + a `connect_diagnostics` report | | | |

### 12.4 The rules of working together
- Every track merges into the main branch daily behind green CI; no branch lives longer than 3 days.
- Cross code review: A reviews C's changes to `ControlChannel`, B reviews C's changes to the tunnel wiring, and C
  reviews B's `Core` interfaces.
- A short integration meeting weekly at every milestone; whatever fails at a milestone is fixed before the next week
  starts.
- The conditional relay (the 7.7 gate) is implemented inside weeks 5 and 6 on tracks A and B without extending the
  schedule.

**The total:** 9 calendar weeks with three developers and a DevOps/QA track. With one developer only the stages become
sequential (about 13 weeks), and the natural order is then: 0 (the prototypes) → 1 and 2 (the server) → 3 (networking)
→ 4 (the application) → 5 → 6.

---

## 13. The risks and their mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| The direct connection failing (CGNAT, corporate networks, no UPnP) | It stops the product being used | A symmetric connection + the server's probe + a prototype with 10 pairs + the relay gate + `ITunnelTransport` ready for the relay |
| TLS 1.3 unavailable on Windows 10 | An error on connecting | `SslProtocols.None` with a floor of 1.2 and the version recorded; the amendment approved by the product owner |
| Windows Firewall blocking the listener silently (especially on the Public profile and managed machines) | It is misdiagnosed as a NAT failure | A rule from the elevated installer + a check when "available" is enabled + `firewall_rule_present` in the diagnostics |
| Corporate Chrome/Edge policies overriding `--proxy-server` or `--user-data-dir` | A session that looks successful while browsing on the user's IP | `PolicyDetector` before launching + the check page afterwards + ending with `browser_not_proxied` |
| The signing certificate being delayed | SmartScreen blocks the installation | Requesting it on day one |
| Antivirus software | False positives | Signing, loopback only, documenting the behaviour |
| The host on a VPN | The exit IP is the VPN's | Detecting the adapter + a warning + recording it |
| The user exhausting the host's resources | The host's machine slows down | The stream and opens-per-second limits |
| One worker | A ceiling on concurrent connections | Enough for hundreds; Redis Pub/Sub documented for the upgrade |

---

## 14. Explicitly out of scope (section 9 of the document)
Remote control, screen sharing, file transfer, reaching the local network, passing the whole device through, phones,
macOS/Linux, more than one user per host, subscriptions, the admin panel on the web, decrypting HTTPS, logging content,
a permanent VPN, reconnecting after a short drop, TCP hole punching, QUIC.

> **Superseded since:** macOS became a supported system in both roles
> ([ADR-0013](decisions/0013-avalonia-and-macos.md)), and user management moved into the application instead of the
> deferred web panel.

---

## 15. The final verification

1. `docker compose up` on the VPS; creating an administrator and users through the CLI; adding `ifconfig.me` and
   `whatismyipaddress.com` to the list.
2. Installing the signed installer on two Windows machines (one of them Windows 10) on two different networks, and
   signing in.
3. Carrying out `docs/acceptance-checklist.md` (18 items) and documenting the result with screenshots, including the
   check page showing the host's IP.
4. The technical checks: `session_keys` empty after the end; no key containers left behind; the logs with no URLs or
   content; the server's traffic not rising during a video in the work browser; nmap on the host's port accepting
   nothing but TLS and cutting off without authentication; CONNECT to private addresses refused.
5. Full CI passing (pytest + xUnit) on the main branch.
6. A `connect_diagnostics` summary documenting the direct-connection success rate and the relay gate's decision.
