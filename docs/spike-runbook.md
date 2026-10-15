# The technical prototype's guide: measuring the direct connection on 10 real pairs

The goal: to feed the relay decision gate (ADR-0003) with real data by the end of week 2. The tool:
`client/tools/Josour.Spike`.

## Requirements on each machine
- Windows 10 or 11 with the .NET 8 SDK (or the tool published as self-contained from CI).
- Allowing the executable in Windows Firewall on the first run (or installing the installer, which adds the rule).
- Knowing the machine's public IP as the internet sees it (`curl ifconfig.me`).

## 1. Checking the certificate and TLS (each machine separately)
```
dotnet run --project tools/Josour.Spike -c Release -- certtest
```
Expected: `tls_loopback: ok (server 1.3 …)` on Windows 11 and `1.2` on Windows 10, and `disposed: True`. Record the
version.

## 2. The candidates and UPnP (each machine separately)
```
dotnet run --project tools/Josour.Spike -c Release -- gather --port 40000 --public-ip <the public IP>
```
Record from the JSON: `upnp_found`, `mapping_ok`, `upnp_external_ip`, `cgnat_suspected`, `ipv6_global`,
`firewall_rule_present`, `vpn_adapter`, `system_proxy_present`.

## 3. The symmetric connection (a pair of machines)
Generate a shared session id and secret once and share them with the other side:
- PowerShell: `[guid]::NewGuid()` and
  `[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`
- macOS/Linux: `uuidgen` and `head -c 32 /dev/urandom | base64`

Machine A (the host):
```
dotnet run --project tools/Josour.Spike -c Release -- symmetric --role host --session <GUID> --secret-b64 <SECRET> --public-ip <IP A> --peer-file endpoint-guest.json
```
Machine B (the user):
```
dotnet run --project tools/Josour.Spike -c Release -- symmetric --role guest --session <GUID> --secret-b64 <SECRET> --public-ip <IP B> --peer-file endpoint-host.json
```
Each side prints a JSON line with its endpoint and writes `endpoint-<role>.json`. Move A's file into B's directory and
the reverse (or paste the JSON line on stdin without `--peer-file`). The options: `--same-public-ip` on both sides if
they are behind the same public IP, `--no-upnp` to skip discovery, `--timeout-s N` (30 by default).

The exit codes: 0 a connection + a successful hello, 2 no connection, 3 a connection but hello failed.

## 4. What is recorded per pair (the measurement table)
| Field | Source |
|---|---|
| Each side's network type (home / office / phone / CGNAT) | By hand |
| `winner`, `tls_version` and `connect_ms` | The resulting JSON (take the host's label for `winner_type`) |
| The failed attempts: `{direction,type,ip,port,ms,stage,error}` | The resulting JSON |
| `inbound_attempts` | The resulting JSON |
| The `gather` diagnostics for both sides | Step 2 |

**The gate's threshold:** at least 10 pairs over two weeks; if the rate of connecting within 10 seconds is below 85%,
the relay service is implemented in weeks 5 and 6.

## 5. What needs verifying on Windows and on real routers (from track B's report)
1. Schannel's key path: re-importing the PFX with `UserKeySet` and `SslStream` accepting it on Win10 and 11, and the
   key container disappearing after `Dispose` (`certutil -user -key`).
2. TLS 1.3 negotiation on Win11 and `SslProtocols.None`'s behaviour on Win10 (ADR-0002).
3. Mono.Nat on two or three routers: discovery within 4 seconds, the mapping succeeding, whether the router honours the
   requested port or returns another, reporting the external IP, and removing the mapping.
4. The accuracy of `cgnat_suspected` on at least one CGNAT network and one double-NAT network.
5. Filtering IPv6 candidates on Windows (temporary addresses and the DAD state).
6. `HostDiagnostics` through `netsh`: detecting the "Josour Tunnel" rule and the `currentprofile` parser on non-English
   Windows, and reading `ProxyEnable`.
7. The listener and the connector in DualMode on Windows, including machines with IPv6 disabled (falling back to IPv4).
8. The Windows Firewall interaction: accepting inbound on the ephemeral port with the rule and without it.
9. The behaviour of `Socket.Close(0)` for connections over capacity on Windows.

---

# The browser launch matrix (week 2)

The goal: to prove the work browser actually goes through the local proxy, and to expose the cases where corporate
policies override the command line. The tool:
`dotnet run --project tools/Josour.Spike -c Release -- browser --self-hosted [--browser chrome|edge]`.

With `--self-hosted` the tool runs `ConnectProxyServer` with an empty site list (everything direct) and opens the
browser on `http://check.josour/`, then prints JSON including: the browser's path, its publisher verification, the
policy state, the launch result, whether a handoff happened, how long the check page took to arrive, and the proxy's
counters.

## The eight cells to be filled in

| # | System | Browser | Policy-managed | Expected |
|---|---|---|---|---|
| 1 | Windows 10 | Chrome | No | `probe_hit_ms` < 10000, `handoff` = false |
| 2 | Windows 10 | Edge | No | The same |
| 3 | Windows 11 | Chrome | No | The same |
| 4 | Windows 11 | Edge | No | The same |
| 5 | Windows 11 | Chrome | Yes (`ProxySettings`) | `policy.proxy_managed` = true, and the check page does not arrive |
| 6 | Windows 11 | Edge | Yes (`ProxySettings`) | The same |
| 7 | Windows 11 | Chrome | Yes (`UserDataDir`) | `policy.user_data_dir_managed` = true |
| 8 | Windows 11 | Chrome | An instance already running with the same profile | `handoff` = true |

To simulate management in cells 5 to 7 (on a test machine only): add the values under
`HKLM\SOFTWARE\Policies\Google\Chrome` or `\Microsoft\Edge` and then delete them after the test.

## What is recorded per cell
`browser`, `path`, `publisher_verified`, `policy.*`, `launch.success`, `launch.failure`, `handoff`, `exit_type_set`,
`probe_hit_ms`, `close_ms`, and `proxy_counters`.

**The acceptance criterion:** cells 1 to 4 give a check page within 10 seconds and a clean close; cells 5 to 8 are
detected before launching or within 10 seconds, so the path ends with `browser_not_proxied` rather than a session
browsing on the user's address without them knowing.

---

# A full session with no interface: the `session` command (week 4)

The goal: to run **a real session between two Windows machines** with the entire production stack — signing in through
`ApiClient`/`AuthSession`, a real control channel over WSS, `TunnelSession` in both roles, the egress policy at the
host, and the local proxy at the user — before the WPF application is ready. The tool is the same
`client/tools/Josour.Spike`, and the command is `session`.

What this command proves and `symmetric` does not: that the server and the client agree on the whole session cycle
(`session.created` ← `endpoint` ← `peer_endpoint` ← `connected` ← `active` ← `stats` ← `end`), and that the traffic
really does leave from the host's address.

## 0. Requirements

- Two machines (or one machine with two different state directories for a dry run), each with the .NET 8 SDK and a copy
  of the repository.
- Two different accounts on the server (`POST /admin/users`). **The user's machine cannot request a device belonging to
  the same user** (`bad_request`).
- A site list with at least one domain to test (`PUT /admin/domains`), such as `api.ipify.org`.
- Allowing the executable in Windows Firewall on the first run (the connect window opens a listener on an ephemeral
  port).

## 1. The exact commands

On the **host machine** (the machine the traffic will leave from), run this first and leave it running:

```
dotnet run --project tools/Josour.Spike -c Release -- session ^
  --api https://<the server> --email host@example.com --password '<the password>' ^
  --role host --available > host.jsonl
```

On the **user's machine**, find the available hosts and then request one of them:

```
dotnet run --project tools/Josour.Spike -c Release -- session ^
  --api https://<the server> --email guest@example.com --password '<the password>' ^
  --role guest --list-hosts

dotnet run --project tools/Josour.Spike -c Release -- session ^
  --api https://<the server> --email guest@example.com --password '<the password>' ^
  --role guest --host-device <the device's id or name> --minutes 30 ^
  --curl-test https://api.ipify.org > guest.jsonl
```

The host accepts the request automatically, so the two sides connect, and then the user prints the local proxy's port
and makes an HTTP GET request through that same proxy with no browser. **The evidence required:** the response body in
the `curl.result` event equals the host's public address (`public_ip` in `hello.ack` on the host's machine), not the
user's machine's address.

Additional options:

| Option | Role | Meaning |
|---|---|---|
| `--available` / `--no-available` | host | Announcing is the default; `--no-available` keeps the machine connected and invisible to any user (to test the channel alone) |
| `--auto-reject` | host | Rejects the first incoming request and then exits with code 0 (to test the rejection path) |
| `--listen-port N` | both | Binds the tunnel's listener to a fixed port; and on the host it is also sent in `host.available.listen_port`, which makes the reachability probe meaningful |
| `--browser chrome\|edge` | user | Launches the real work browser on the check page instead of `--curl-test` (the two do not combine: with a browser the owning-PID check is enabled, so the tool's own request is refused) |
| `--profile <path>` | user | The browser's profile directory |
| `--minutes N` | user | The requested session duration (1..`max_session_minutes`) |
| `--no-upnp` | both | Skips UPnP discovery (faster on a network with no cooperative router) |
| `--connect-timeout-s N` | both | The symmetric connection's timeout (30 by default, which is the server's own timeout) |
| `--stats-interval-s N` | host | How often `session.stats` is sent (30 by default) |
| `--state-dir <path>` | both | The state directory beside the executable (`spike-state/` by default): `settings.json`, the device secret and the refresh token. **Two identities on the same machine = two different directories** |
| `--reset-device` | both | Wipes the device identity and the tokens, so the next sign-in registers a new device (after the device is revoked on the server) |
| `--quiet` | both | Stops the human lines on stderr and keeps the JSON only |

The device secret is stored in `<state-dir>/secrets/` with DPAPI on Windows, and as marked plaintext off Windows
(development only). Do not move that directory between machines.

`Ctrl+C` at any moment carries out the whole cleanup order in section 7 of `docs/protocol.md` and sends `session.end`
with the reason for the role (`host_ended` or `guest_ended`) instead of killing the process.

## 2. The event stream: JSON on stdout and a human summary on stderr

Every event is one JSON line. The envelope is fixed:

```json
{"ts":"2026-09-05T09:12:33.4210000Z","seq":14,"role":"guest","event":"tunnel.connected","winner_type":"upnp","connect_ms":812,"tls_version":"1.3","proxy_port":51544,"probe_url":"http://check.josour/"}
```

`ts` is ISO-8601 UTC, `seq` is a counter starting at 1, and `role` is `host` or `guest`. The human lines go to stderr,
so `> run.jsonl` gives a file fit for analysis while you keep watching on the screen.

**Setup and sign-in**

| Event | Fields | Meaning |
|---|---|---|
| `tool.start` | `api`, `state_dir`, `os`, `reset_device` | The run begins |
| `auth.signed_in` | `user_id`, `email`, `display_name`, `device_id`, `device_name`, `app_version` | The sign-in succeeded; `device_id` is what goes into `--host-device` on the other side |
| `auth.failed` | `code`, `status`, `message` | The sign-in failed (`invalid_credentials`, `account_locked`, `device_revoked`, `unavailable`) |
| `channel.state` | `state` | `Connecting` / `Connected` / `Reconnecting` / `Disconnected` |
| `channel.closed` | `reason`, `close_code`, `description` | A terminal close with no reconnection after it (4401 / 4403 / 4409) |
| `channel.connect_failed` | `error` | The channel never opened |
| `hello.ack` | `server_time`, `public_ip`, `allowed_ports`, `allowlist_version`, `max_session_minutes`, `request_timeout_seconds`, `log_domains` | **`public_ip` here on the host's machine is the address the sites must see** |
| `allowlist.loaded` | `version`, `entries`, `skipped` | The site list from `GET /domains`; `skipped` is the entries the parser refused |
| `frame.in` | `type` + a summary | Every frame inbound from the server (including `ping`/`pong`) |
| `frame.out` | `type` | Every frame outbound from the session cycle |

**The request cycle**

| Event | Fields | Meaning |
|---|---|---|
| `host.available` | `available`, `listen_port` | The host announced itself (or did not, with `--no-available`) |
| `hosts` | `count`, `hosts[]` | A snapshot of the hosts (`device_id`, `user_display_name`, `device_name`, `reachable`) |
| `host.selected` | `device_id`, `device_name`, `reachable` | The host that matched `--host-device` |
| `host.not_found` | `wanted`, `available` | No host matches, or more than one matches (ambiguity = no choice) |
| `request.created` | `request_id`, `expires_at` | The server accepted the request and it is pending |
| `request.incoming` | `request_id`, `guest_name`, `guest_device`, `duration_min`, `expires_at` | The request reached the host |
| `request.accepted` / `request.rejected` | `request_id` | The host's automatic decision |
| `request.result` | `request_id`, `accepted`, `reason` | The request's outcome at the user (`rejected`/`expired`/`cancelled`/`host_unavailable`) |
| `request.rejected_by_server` | `code`, `message` | `request.create` was refused (`host_unavailable`, `session_exists`, `request_pending`, `bad_request`…) |
| `session.created` | `session_id`, `peer_display_name`, `peer_device_name`, `peer_public_ip`, `same_public_ip`, `expires_at` | The session began; from here `SessionCoordinator` takes over (the same session coordinator the WPF application drives) |

**The tunnel**

| Event | Fields | Meaning |
|---|---|---|
| `tunnel.state` | `state` | `Listening` → `Connecting` → `Authenticating` → `Connected` → `Ended` |
| `tunnel.prepared` | `listen_port`, `cert_fp_sha256`, `candidates[]` | What was sent in `session.endpoint`: the certificate fingerprint and the candidates (`lan`/`v6`/`upnp`/`public`) |
| `tunnel.peer_endpoint` | `cert_fp_sha256`, `candidates[]` | The other side's candidates; the parallel connecting began |
| `tunnel.connected` | `winner_type`, `connect_ms`, `tls_version`, `proxy_port`, `probe_url` | **The tunnel succeeded.** `winner_type` at the host is the authoritative one, and `tls_version` must be `1.3` on Win11 and `1.2` on Win10 |
| `tunnel.connect_failed` | `reason`, `role`, `candidates[]` with each candidate's timing and error | The connection failed; this is the same diagnostic uploaded in `session.connect_failed`, and it is what fed the relay gate |
| `tunnel.died` | `suggested_reason` | The tunnel died after having worked (the other side disappeared or the PONG timeout expired) |
| `proxy.ready` | `port`, `probe_url` | The local proxy's port on 127.0.0.1 at the user |
| `curl.result` | `url`, `status`, `elapsed_ms`, `bytes`, `body_prefix` | **The evidence:** an HTTP GET request through the proxy succeeded, and `body_prefix` is the first 200 characters of the response |
| `curl.failed` | `url`, `error` | The request through the proxy failed |
| `browser.launch` | `browser`, `success`, `failure`, `detail`, `handoff`, `pid` | The result of launching the work browser with `--browser` |
| `session.active` | `expires_at` | The server moved the session to active |
| `session.stats` | `bytes_up`, `bytes_down`, `open_streams` | From the host every 30 seconds (the same as what is sent in `session.stats`) |
| `session.terminate` | `reason` | The server ended the session |
| `session.ending` / `session.ended` | `kind`, `reason`, `bytes_up`, `bytes_down`, `domains`, `server_already_ended` | The cleanup order started / completed |
| `tool.exit` | `code`, `kind`, `end_reason`, `bytes_up`, `bytes_down`, `domains` | Always the last line; `code` is the process's exit code |

The `kind` values in `session.ended` and `tool.exit`: `Terminated` (the server ended it), `Expired` (the duration ran
out), `LocalEnd` (Ctrl+C or a deliberate end), `ConnectFailed`, `TunnelDied`, `ChannelLost` (the control channel
dropped during the session), `ProtocolError` (a breach of the contract, or `browser_not_proxied` with `--browser`).

Without `--browser` there is no work browser at all, so there is no waiting for the check page and no
`browser_not_proxied`: the proxy stands up and stays, and `--curl-test` proves it by itself. With `--browser` the
behaviour stays complete — the browser is launched at `session.active`, and the check page not arriving within 10
seconds ends the session with `browser_not_proxied`.

## 3. The exit codes

| Code | Meaning | What it means for QA |
|---|---|---|
| `0` | The session worked and ended as expected, or `--list-hosts` succeeded, or `--auto-reject` rejected | Success |
| `1` | Misused arguments, or no host matches `--host-device` | An error in the command, not in the product |
| `2` | No tunnel stood up: the symmetric connection failed, or the request was rejected or timed out, or the host is unavailable | Recorded in the relay gate's table |
| `3` | The tunnel stood up and then died, or the control channel dropped during the session | A network or stability failure |
| `4` | An authentication failure or a breach of the contract by the server | Raised to track A or C |
| `130` | Ctrl+C before the session stood up | Not a failure |

## 4. What QA records per run

Keep `host.jsonl` and `guest.jsonl` complete, and fill in from the `tunnel.connected` and `tool.exit` events:

| Field | From where |
|---|---|
| Each side's network type (home / office / phone / CGNAT) | By hand |
| `winner_type`, `connect_ms`, `tls_version` | `tunnel.connected` on the host's machine (its label is the authoritative one) |
| The failed attempts `{direction,type,ip,port,ms,stage,error}` | `tunnel.connect_failed.candidates` |
| `public_ip` for each side | `hello.ack` on each machine |
| The IP the site saw | `curl.result.body_prefix` on the user's machine |
| `bytes_up` / `bytes_down` / `domains` | `tool.exit` on the host's machine |
| Each side's exit code | `tool.exit.code` |

**The acceptance criterion for a successful run:** exit code `0` on both sides, `tunnel.connected` within 10 seconds of
`session.created`, `curl.result.status` equal to 200, `body_prefix` matching the host's `public_ip`, and
`session.ended.kind` not being `TunnelDied`.

## 5. Common failures and what they mean

| What appears | The explanation |
|---|---|
| `request.rejected_by_server` with `host_unavailable` | The host is not connected, or did not announce `--available`, or has an unfinished session, or is answering another request |
| `request.rejected_by_server` with `bad_request` | Both machines belong to the same user — use two different accounts |
| `request.rejected_by_server` with `session_exists` | An earlier session was not ended; end it from `POST /admin/sessions/{id}/terminate` |
| `host.not_found` with `available > 0` | The name matches more than one host; use the `device_id` from `--list-hosts` |
| `tunnel.connect_failed` with `peer_endpoint_timeout` | The other side did not send `session.endpoint` within the timeout (or the server did not pass it on) |
| `tunnel.connect_failed` with every candidate at `stage=tcp` | Inbound is blocked on both sides — this is the case that opens the relay gate |
| `curl.failed` with 403 while the domain is in the list | The host refused the address after resolution (`private_ip`): the name resolves to an internal address on the host's network |
| `session.ended.kind = ChannelLost` | The control channel dropped, so the server ended the session with `*_disconnected` |
| `session.ended.kind = TunnelDied` with `session.end` carrying `guest_disconnected`/`host_disconnected` | The tunnel died while both channels were alive: **the name is the peer's name**, not the sender's (ws-protocol section 9) |
| `session.ended.kind = ProtocolError` with `browser_not_proxied` (with `--browser` only) | The work browser started but did not go through the proxy (a corporate policy or a handoff to an existing instance) |
| `channel.closed` with code `4409` | Another copy of the tool is running with the same `--state-dir` on another machine |

## 6. What has not been verified on Windows yet (from track B's week-4 report)

Added to the list in section 5 above:

10. A full session between two real Windows machines: `session --role host` and `session --role guest` through to
    `tool.exit` with code 0 on both sides.
11. `curl.result.body_prefix` equalling the host's `public_ip` (proof of egress from its address).
12. `--browser` on Win10 and Win11: launching the work browser from inside the session, the check page arriving, then
    it being closed in step 2 of the cleanup order at `Ctrl+C`.
13. `Ctrl+C` on both sides: `session.end` arriving with the right reason, and the listener closing and the UPnP
    mappings being removed (`netsh` and `certutil -user -key` afterwards).
14. `--listen-port N` on the host: `reachable=true` appearing in `hosts` at the user.
15. The device secret under DPAPI in `<state-dir>/secrets/` on Windows, and `--reset-device` re-registering after the
    device is revoked.

---

# The system proxy spikes S1–S3 (ADR-0015, phase 0 of the restructure)

The goal: to settle how the guest app changes the operating system's proxy ([ADR-0015](decisions/0015-system-proxy-instead-of-work-browser.md))
before any of it is built. S1 decides the macOS path (`networksetup` called directly, or a small root-installed
helper); S2 confirms the WinINet per-user path on Windows and its Group Policy and VPN edges; S3 lists what still
leaks around a system proxy. The tools, all in `scripts/spikes/`:

| File | What it is |
|---|---|
| `system-proxy-macos.sh` | S1: saves, applies, reads back and restores the proxy of every enabled network service with `networksetup`, timing each call |
| `system-proxy-windows.ps1` | S2: the same through `InternetSetOptionW` per-connection options (LAN and every dial-up/VPN connection) |
| `dummy-listener.pl`, `pages/` | The local listener the macOS script starts (the Windows script carries the same thing in PowerShell) and the pages it serves |
| `proxy-probe/` | A .NET 8 console that shows whether a running .NET process notices the change |

## 0. Before running anything

- **What changes.** Only the proxy settings: on macOS the web proxy, secure web proxy, bypass domains, auto-proxy
  (PAC) state and auto-discovery state of each enabled network service; on Windows the connection flags, proxy server
  and bypass list of the LAN settings and of each dial-up/VPN connection. Both scripts save the current values to a
  snapshot file **before** the first change, restore from it at the end (also on Ctrl+C), read everything back and say
  whether it is exactly as before. Exit code `0` means restored exactly; `2` means the restore did not match (the
  summary says which field).
- **While the proxy is applied, the internet is off** for every app that honours the system proxy: the dummy
  listener on `127.0.0.1:18923` answers 502 to everything and forwards nothing anywhere. Pick a moment when a few
  minutes offline do not matter (not during a call). Anything that keeps working during that window is going around
  the proxy, which is exactly what S3 looks for.
- **If a run is killed** (terminal or window closed, machine crashed), the snapshot path is printed before the apply:
  - macOS: `scripts/spikes/system-proxy-macos.sh --restore ~/josour-spike-results/s1-macos-<stamp>/snapshot.tsv`
  - Windows: `powershell -ExecutionPolicy Bypass -File scripts\spikes\system-proxy-windows.ps1 -RestoreFrom %USERPROFILE%\josour-spike-results\s2-windows-<stamp>\snapshot.json`
  - By hand: macOS System Settings > Network > (service) > Details > Proxies; Windows Settings > Network & internet >
    Proxy, and Internet Options > Connections for a VPN connection's own settings.
- **Copy the whole `scripts/spikes` folder** to the test machine: the scripts refuse to apply when `pages/` (and on
  macOS `dummy-listener.pl`) is not next to them.
- **The poller.** After the script starts its listener it prints `http://127.0.0.1:18923/`. Open that in every
  browser under test **before** pressing Enter to apply, and keep the windows visible side by side (browsers slow down
  timers in background tabs). Each page asks twice a second for `http://check.josour/ping`, a name that does not exist:
  red until the browser hands it to the system proxy, green while it does. The script prints
  `+ chrome reached the listener +0.4 s after the apply finished` live, and the summary has the first and last request
  of each browser. Add `?b=name` to the page's address to give a browser another name (two Chrome profiles, say).
- **Firefox** must be in Settings > Network Settings > *Use system proxy settings* (the default). Turn HTTPS-Only Mode
  off for the test, or allow `check.josour`: otherwise the poller stays red while the listener logs `firefox-https`.
- **The .NET probe** needs the .NET 8 SDK. Start it in a second terminal before the apply and stop it after the
  restore (section 4).
- **Never run either script with `sudo` or from an elevated window.** The question is what the app, running as the
  user, may do. The macOS script refuses root; the Windows script warns and records `elevated: True`.

## 1. S1: macOS (`networksetup` authorisation)

The matrix: macOS 14 and macOS 15 (whatever machines exist), each with an **administrator** account and a
**standard** account.

1. **Dry run** (reads only; shows the services it would touch, their current settings, and whether the port is free):
   ```
   scripts/spikes/system-proxy-macos.sh --dry-run
   ```
2. **Run 1, administrator account.** Start the probe in a second terminal (section 4), then:
   ```
   scripts/spikes/system-proxy-macos.sh --bypass <the Josour server host> --bypass <the relay host>
   ```
   (`--bypass` is optional; the default list already covers `*.local`, loopback and the private ranges.) Open the
   poller in Safari, Chrome, Firefox and Edge, press Enter to apply, and **watch the screen**: the terminal prints
   each `networksetup` call as it finishes, so a dialog can be tied to the call that raised it. If a dialog appears,
   write down its exact wording and whose password it asks for (yours, or "an administrator's"). Enter the password
   (or Cancel: that is a finding too). When asked, type how many dialogs appeared. Look at the browsers (section 3),
   then press Enter to restore and answer the second count.
3. **Run 2, administrator account, a long hold:**
   ```
   scripts/spikes/system-proxy-macos.sh --hold 400
   ```
   If run 1 asked only once, this shows whether the restore asks again after the authorisation has timed out. The
   app's restore can come hours after the apply, from a guardian process or at the next login, with nobody there to
   type a password.
4. **Run 3, standard account.** Log in to a standard account with Fast User Switching (a dialog belongs to the
   graphical session, so `su` in Terminal is not the same test). Copy the folder somewhere readable first, for
   instance `cp -R scripts/spikes /Users/Shared/josour-spikes`, then repeat run 1 from there. If a dialog asks for an
   administrator, Cancel once (to record the failure) and, on a second run, enter the administrator's credentials
   once (to record whether it then stays silent).

What each call is marked as in the summary: `silent` (under 1.5 s, no error), `slow` (1.5 s or more: in practice a
dialog was on screen) or `failed` (a non-zero exit or an error message, quoted in full in `commands.tsv`). The files in
`~/josour-spike-results/s1-macos-<stamp>/`: `summary.txt`, `commands.tsv`, `snapshot.tsv`, `readback-applied.txt`,
`readback-restored.txt`, `scutil-before/applied/after.txt`, `listener.log`, `listener-hosts.txt`.

Two things to note by hand if they happen: a service whose settings come back slightly different from what was set
(the summary prints the difference, for instance how `networksetup` writes `::1` or `10.0.0.0/8`), and a restore
that leaves `127.0.0.1` in the server field of a proxy that was empty before (`networksetup` may not accept `""` to
clear it).

## 2. S2: Windows (WinINet per-user)

The matrix: Windows 10 and Windows 11, each with a **standard** account and a non-elevated **administrator**
account, plus the Group Policy and VPN cases on one test machine. Use Windows PowerShell 5.1 (`powershell`), which
every Windows has.

1. **Dry run** (reads the settings of every connection and the proxy policies; changes nothing):
   ```
   powershell -ExecutionPolicy Bypass -File scripts\spikes\system-proxy-windows.ps1 -DryRun
   ```
2. **Run 1, from a normal (non-elevated) window.** Start the probe first (section 4), then:
   ```
   powershell -ExecutionPolicy Bypass -File scripts\spikes\system-proxy-windows.ps1 -Bypass <server host>,<relay host>
   ```
   Open the poller in Edge, Chrome and Firefox, press Enter to apply, look at the browsers, press Enter to restore.
3. **Run 2, standard account.** The same.
4. **The Group Policy lock (test machine only).** As administrator:
   ```
   reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings" /v ProxySettingsPerUser /t REG_DWORD /d 0 /f
   ```
   Rerun as the standard user and as the non-elevated administrator. The script reports the lock and asks whether to
   apply anyway: answer `y`, to record what WinINet does under it (fails, or succeeds without effect). Afterwards:
   ```
   reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings" /v ProxySettingsPerUser /f
   ```
5. **VPN.** With a Windows built-in VPN connection (Settings > Network & internet > VPN) **connected**, the dry run
   must list `RAS '<its name>'` next to `LAN`; run once while connected. With a third-party VPN client (the kind that
   adds a network adapter), run once while connected and note whether the browsers still reach the listener.
   `-LanOnly` touches the LAN settings alone, to see what breaks when the dial-up/VPN connections are left out.

The files in `%USERPROFILE%\josour-spike-results\s2-windows-<stamp>\`: `summary.txt`, `commands.tsv`,
`snapshot.json`, `listener.log`, `listener-hosts.txt`. The summary also shows the legacy registry values
(`ProxyEnable`, `ProxyServer`, `ProxyOverride`, `AutoConfigURL`) before, during and after, and every proxy policy it
found for Internet Explorer settings, Chrome, Edge and Firefox.

## 3. Do the browsers pick it up, and how quickly?

Read it off the poller and section 3 of the summary:

- **Picked up without a restart:** the page went green without being reloaded and without restarting the browser.
- **How quickly:** `first +N s after the apply`.
- **Let go after the restore:** `last +N s after the restore`. Around zero or negative is immediate; a large positive
  number means the browser kept using a proxy that was no longer set (and would have had no internet once the
  listener stopped).
- Also try once: a browser **started** while the proxy is applied, and `chrome://net-internals/#proxy` /
  `edge://net-internals/#proxy` to see the effective settings the browser itself reports.

## 4. Does .NET re-read the system proxy?

From the repository root, in a second terminal, before the apply:
```
dotnet run --project scripts/spikes/proxy-probe -- 900
```
It prints one line per change, for instance
`10:15:03.120 DefaultProxy says http://127.0.0.1:18923/; kept client went through the proxy; fresh client went through the proxy`.
The *kept* client is created once at start-up, like the app's `ApiClient`; the *fresh* one is created each second.
Record whether `DefaultProxy`, the kept client and the fresh client follow the apply, and whether they come back to
`direct` after the restore. Its requests also show up in the summary as `dotnet-kept` and `dotnet-fresh`. A note that
`HTTP_PROXY` (or a sibling) is set means .NET uses that variable instead of the system proxy: unset it and start
again.

## 5. S3: the leak survey (by hand, during a hold)

The rule: while the proxy points at the dummy listener nothing can load *through* it, so anything that loads, or any
packet that leaves for a public address, is going *around* it. Run these during a long hold (`--hold 600` /
`-HoldSeconds 600`), with a capture running:

- macOS: `sudo tcpdump -n -l -i any 'port 53 or udp port 443 or udp port 3478 or udp port 19302'` (if `-i any` is
  refused, use the Wi-Fi interface, usually `-i en0`).
- Windows: Wireshark if it is installed, with the capture filter `port 53 or udp port 443 or udp port 3478 or udp port 19302`.
  Otherwise, in an elevated window: `pktmon filter remove`, then `pktmon filter add DNS -p 53`,
  `pktmon filter add QUIC -t UDP -p 443`, `pktmon filter add STUN -t UDP -p 3478`,
  `pktmon filter add STUN2 -t UDP -p 19302`, `pktmon start --capture --pkt-size 0 --file-name %TEMP%\josour-s3.etl`;
  after the tests `pktmon stop` and `pktmon etl2pcap %TEMP%\josour-s3.etl --out %TEMP%\josour-s3.pcapng` to open in
  Wireshark. Run `ipconfig /flushdns` before the DNS check.

| # | Check | How | Leak if |
|---|---|---|---|
| 1 | QUIC (HTTP/3 over UDP) | Before the run, open https://www.google.com, https://www.youtube.com and https://www.cloudflare.com in each browser so it learns they speak HTTP/3. During the hold, reload them | The page loads, or UDP 443 to a public address appears in the capture |
| 2 | WebRTC STUN | Open `http://127.0.0.1:18923/webrtc` and press Run once **before** the apply (a baseline: a srflx candidate is normal) and once during the hold | A `srflx` candidate during the hold: the page learned this computer's own public address |
| 3 | DNS prefetch | Open `http://127.0.0.1:18923/dns` during the hold and search the capture for the token it shows (`... \| grep --line-buffered <token>` on macOS) | A query for `dp-`, `pc-`, `a-` or `img-<token>.example.com` |
| 4 | Apps that ignore the proxy | Use the computer normally for a minute during the hold: mail, chat, updates, the music app | An app that keeps working ignores the system proxy; `listener-hosts.txt` lists the ones that honoured it |

Write down, next to the results, whether encrypted DNS is on (Chrome and Edge *Use secure DNS*, Firefox *DNS over
HTTPS*, a DNS profile on macOS): with it, lookups do not appear on port 53 at all. Also note whether iCloud Private
Relay is on (macOS).

## 6. The results tables

**S1, one column per macOS version and account.** In each cell: `silent`, `slow + dialog` (with its wording) or
`failed` (with the message).

| | macOS 14 admin | macOS 14 standard | macOS 15 admin | macOS 15 standard |
|---|---|---|---|---|
| `-setwebproxy` | | | | |
| `-setwebproxystate` | | | | |
| `-setsecurewebproxy` | | | | |
| `-setsecurewebproxystate` | | | | |
| `-setproxybypassdomains` | | | | |
| `-setautoproxystate` | | | | |
| `-setproxyautodiscovery` | | | | |
| Dialogs counted: apply / restore | | | | |
| Restore after `--hold 400` asked again? | | | | |
| Read-back after the apply (`as set`?) | | | | |
| Restore (`exactly as before`?) | | | | |
| `scutil --proxy` followed the apply / the restore (ms) | | | | |

**S2, one row per machine and case.**

| Windows (build) | Account | Elevated | Case (plain / GP lock / built-in VPN / VPN client) | `set per-connection` | Notifications | Read-back | Restore exact | Notes |
|---|---|---|---|---|---|---|---|---|
| | | | | | | | | |

**Browsers (sections 3 and 4), one row per OS and browser.**

| OS | Browser (version) | Proxy mode | Picked up without a restart? | First request after the apply (s) | Last request after the restore (s) | Notes |
|---|---|---|---|---|---|---|
| macOS | Safari | system | | | | |
| macOS | Chrome | system | | | | |
| macOS | Firefox | Use system proxy settings | | | | |
| macOS | Edge | system | | | | |
| Windows 10 | Edge | system | | | | |
| Windows 10 | Chrome | system | | | | |
| Windows 10 | Firefox | Use system proxy settings | | | | |
| Windows 11 | Edge | system | | | | |
| Windows 11 | Chrome | system | | | | |
| Windows 11 | Firefox | Use system proxy settings | | | | |

| OS | `DefaultProxy` followed the apply? | Kept client went through the proxy? | Fresh client? | All back to direct after the restore? (s) |
|---|---|---|---|---|
| macOS | | | | |
| Windows | | | | |

**S3, one column per browser.** In each cell: `no leak`, or what was seen.

| Check | macOS Safari | macOS Chrome | macOS Firefox | Windows Edge | Windows Chrome | Windows Firefox |
|---|---|---|---|---|---|---|
| 1. QUIC | | | | | | |
| 2. WebRTC srflx during the hold | | | | | | |
| 3. DNS prefetch (which of dp/pc/a/img) | | | | | | |
| 4. Apps that kept working (per machine) | | | | | | |

## 7. What to send back

- Every `summary.txt`: one per macOS version and account, one per Windows machine and case. Each says at the top
  which run it was. They name the network services, the previous proxy settings and the proxy policies, and
  `listener-hosts.txt` lists the hosts other apps tried to reach, so read them before sharing.
- The probe's output lines (copy them from the terminal).
- The tables in section 6, filled in, and the wording of any dialog.

## 8. How the results decide phase 4

- **S1.** Every call `silent` on both accounts, including the restore after `--hold 400`: `MacSystemProxyController`
  calls `networksetup` directly. Any dialog or failure on either account: the small C helper, installed once as root
  and accepting two commands only. A dialog on the first call alone still counts, because the guardian and the
  restore at login cannot answer one.
- **S2.** `set per-connection` succeeding and an exact restore on a standard account confirm the plan. Under
  `ProxySettingsPerUser=0` the app turns the guest role off with a clear message, whatever WinINet did. If browsers
  ignore the LAN settings while a dial-up/VPN connection is up, the controller sets every such connection too.
- **Browsers.** A browser that needs a restart gets told so at session start (the check page catches it). A browser
  that keeps a stale proxy after the restore shapes the cleanup order and the "repair internet settings" command.
- **.NET.** If `DefaultProxy` follows the change, `LoopSafeWebProxy` has to wrap every client the app owns and keep the
  proxy as it was before the session. If it does not, it is still needed, and the stale value is written down.
- **S3.** Whatever leaks goes into ADR-0015's consequences and into the host's disclosure. Fixing a leak through
  browser policy (Chrome and Edge `QuicAllowed`, WebRTC handling) is a separate decision, not part of phase 4.
