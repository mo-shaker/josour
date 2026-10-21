# ADR-0015: the guest's system proxy instead of a dedicated work browser

**Status:** proposed on 2026-10-13; **accepted and implemented on 2026-10-21** (client 2.0.0). Supersedes the "scope of
enforcement" paragraph of [ADR-0010](0010-route-all-through-host.md) and acceptance criterion 10; replaces the work-browser
and owner-check seams of [ADR-0013](0013-avalonia-and-macos.md).

## Context

The guest's traffic went through the host only from an isolated "work browser" the app launched, and the local proxy
admitted only that browser's processes (the owner-PID check, SECURITY.md boundary 2). The owner asked that, once
connected, the guest can use **any browser** — no dedicated browser at all.

## Decision

1. **While a session is active, the guest app points the operating system's proxy at its local proxy**
   (`127.0.0.1:<port>`, HTTP and HTTPS), with a bypass list for loopback, link-local and private addresses and `.local`
   names, and restores the previous settings when the session ends.
   - **Windows:** the current user's WinINet per-connection options (`InternetSetOptionW`), on the LAN settings and every
     dial-up/VPN connection, with automatic detection and the PAC script turned off for the session; no administrator
     rights. A Group Policy that keeps proxy settings per machine (`ProxySettingsPerUser=0`) is reported as
     `policy_locked` and the session ends with `system_proxy_failed`.
   - **macOS:** `networksetup` on every enabled network service (web and secure web proxy, bypass list, PAC off,
     auto-discovery off). When `networksetup` refuses for lack of authorisation, the same batch of commands runs once
     through `osascript … with administrator privileges` — macOS's own password dialog; a cancelled dialog is
     `authorization_denied`. The "authenticated proxy" flag is never touched, since we never hold that password.
2. **Fail closed, restore always.** A fixed proxy rather than a PAC with a `DIRECT` fallback, so a dead app stops
   browsing rather than silently leaking the guest's own address. The settings come back through, in order of
   likelihood:
   - the session's own cleanup (step 2 of docs/protocol.md section 7), with a second attempt after the tunnel is gone;
   - log-off, shut-down and SIGTERM (`ShutdownRequested`, `ProcessExit`);
   - a **guardian** — the same executable with `--proxy-guardian <pid> <start>`, no window — that restores the moment the
     app dies;
   - the next login: a one-shot `RunOnce` entry on Windows, a launch agent running `--restore-proxy` on macOS;
   - the next start of the app, before sign-in;
   - **"Repair internet settings"** in the tray.

   All of them work from `system-proxy.json`, written **before** anything changes (the change is refused if it cannot be
   written) and removed only after the settings are back. A restore only puts back what still points at
   `127.0.0.1:<port>`, so a change the user made during the session is kept.
3. **Nothing leaves directly by accident.** With every site allowed (the default, ADR-0010), a port outside
   `allowed_ports`, an address literal and an `OPEN_FAIL` are refused (`403`/`502`) instead of going out of the guest's
   own connection. A direct path exists only in the host's enforce mode, where it is the point. The app's own traffic
   (API, control channel, the proxy's direct path) never enters its own proxy: `HttpClient.DefaultProxy` is wrapped by
   `LoopSafeWebProxy`, which goes direct whenever the system would send it to our own listener.
4. **The process check becomes an account check.** The listener stays on loopback; on Windows a connection is admitted
   only from a process of the same Windows account (owner from `GetExtendedTcpTable`, account from its token), and a
   connection whose owner cannot be identified is admitted — the check narrows the loopback boundary and must never
   break browsing on its own. macOS relies on the loopback listener (an `lsof` per connection of a whole machine's
   browsing is too costly).
5. **Plain `http://` goes through the tunnel** instead of being redirected to https, which would break http-only sites
   and certificate checks (OCSP, CRL) once every browser is behind the proxy. The host can read such pages; the guest's
   session panel says so.
6. **The check page is a confirmation, not a gate.** After the proxy is set, the default browser opens
   `http://check.josour/` (a setting, on by default). If it does not come through within 10 s the guest sees a warning —
   that browser may ignore the system proxy — and the session runs on. `CONNECT check.josour:443` is answered `502` at
   once so browsers that try https first fall back to the page.

## Not measured first

Phase 0 of the plan was to measure, on real machines, whether `networksetup` asks for authorisation and how quickly
browsers pick a change up (`docs/spike-runbook.md`, S1–S3). The owner chose to ship without the measurement and to fix
what a real installation shows. The implementation covers each outcome the spike could have had: silent success,
macOS's own dialog, and refusal (then our administrator prompt). What is therefore unverified until the first real
sessions: how often macOS prompts, and whether every browser follows the change without being restarted.

## Consequences

- The product does become device-wide for applications that honour the system proxy, which ADR-0010 deliberately
  ruled out. The host's disclosure now says "every program on their computer that follows the system proxy settings".
- Not covered: UDP (WebRTC can still reveal the guest's own address, QUIC falls back to TCP through the proxy) and
  applications that ignore the system proxy. A network service that appears during a session (a VPN that comes up) is
  not re-pointed until the next session.
- macOS has no command to clear a proxy's server field, so a proxy that was off before the session is turned off again
  with our address left in its (disabled) field.
- `Josour.Browser`, the work-browser guard and the macOS `lsof` owner check are removed. Clients announce the
  `system_proxy` capability in `hello`.
