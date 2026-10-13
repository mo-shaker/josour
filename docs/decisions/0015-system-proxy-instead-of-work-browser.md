# ADR-0015: the guest's system proxy instead of a dedicated work browser

**Status:** proposed on 2026-10-13. Will supersede the "scope of enforcement" paragraph of
[ADR-0010](0010-route-all-through-host.md) and acceptance criterion 10 once implemented; amends the macOS owner check of
[ADR-0013](0013-avalonia-and-macos.md).

## Context

The guest's traffic went through the host only from an isolated "work browser" the app launched, and the local proxy
admitted only that browser's processes (the owner-PID check, SECURITY.md boundary 2). The owner asked that, once
connected, the guest can use **any browser** — no dedicated browser at all.

## Decision

1. **While a session is active, the guest app points the operating system's proxy at its local proxy**
   (`127.0.0.1:<port>`, HTTP and HTTPS), with a bypass list for local and private addresses and for the Josour server
   and relay; it restores the previous settings when the session ends. Windows: WinINet per-connection options (no
   administrator rights). macOS: `networksetup` per enabled network service (authorisation to be settled by a spike).
2. **Fail closed, restore always.** A fixed proxy rather than a PAC with a `DIRECT` fallback, so a dead app stops
   browsing rather than silently leaking the guest's own address. A marker written *before* applying, a guardian
   process, a restore at the next login and at the next start, and a "repair internet settings" command; a restore only
   ever puts back settings that are still ours.
3. **Nothing leaves directly by accident.** A port outside `allowed_ports` is refused (`403`) instead of going out of
   the guest's own connection; IP literals stay refused. The app's own traffic (API, control channel, relay) never goes
   into its own proxy.
4. **The process check becomes a user check.** The listener stays on loopback; on Windows a connection is admitted only
   from a process of the same OS user.
5. **Plain `http://` goes through the tunnel** instead of being redirected to https, which would break http-only sites
   and certificate checks once every browser is behind the proxy. The host can read such pages; the disclosure says so.

## Consequences

- The product does become device-wide for applications that honour the system proxy, which ADR-0010 deliberately
  ruled out. The host's disclosure must say "every app on the guest's computer that uses the system proxy".
- Not covered: UDP (WebRTC can still reveal the guest's own address) and applications that ignore the system proxy.
- `Josour.Browser` and the work-browser guard are removed.
