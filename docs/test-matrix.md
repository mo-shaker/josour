# The test device and environment matrix

Used in weeks 7 and 8 to run the 18-item acceptance list, and the relay gate decision is built on it.

## 1. The devices required

| Code | System | Usual role | Purpose |
|---|---|---|---|
| W10 | Windows 10 (22H2, build ≥ 19045) | Host and user | Proves TLS 1.2 works over Schannel (ADR-0002) and the behaviour of toasts and the tray on the older system |
| W11 | Windows 11 (23H2 or newer) | Host and user | Proves TLS 1.3 negotiation, the Mica appearance, and multi-monitor DPI |
| W11-MDM | Windows 11 managed by Intune or GPO | User | Proves browser-policy detection and ending the session with `browser_not_proxied` |
| SRV | A small Linux VPS (1 vCPU / 2 GB) | The server | Staging and then production |

The minimum to start: W10, W11 and SRV. A W11-MDM machine can be simulated on W11 by adding the policy keys by hand and
then deleting them (see `docs/spike-runbook.md`).

## 2. The networks required

| Code | Description | What it reveals |
|---|---|---|
| N-HOME-A | A home with public IPv4 and UPnP enabled | The happy path: the UPnP mapping succeeds |
| N-HOME-B | A second home with a different provider | A home-to-home pair, the likeliest to succeed directly |
| N-OFFICE | An office network behind a corporate firewall | Inbound is blocked; proves the value of the symmetric connection |
| N-CGNAT | A fixed line or a phone behind CGNAT | No inbound reachability at all; feeds the relay decision |
| N-PROXY | A network that forces a proxy for egress | Proves the system proxy is respected in WSS and on the direct path |

## 3. The test pairs for the relay gate

At least ten pairs over two weeks per `docs/spike-runbook.md`. The target distribution:

| # | Host | User | Expectation |
|---|---|---|---|
| 1-3 | N-HOME-A | N-HOME-B | Direct success over `upnp` or `public` |
| 4-5 | N-HOME-A | N-OFFICE | Success if the host is reachable |
| 6-7 | N-OFFICE | N-HOME-B | Depends on the symmetric connection: the user is the listener |
| 8-9 | N-CGNAT | N-HOME-A | Direct failure expected from the CGNAT side |
| 10 | N-HOME-A | N-PROXY | Reveals the need for a relay on port 443 |

**The threshold:** below 85% success within 10 seconds means implementing the relay in weeks 5 and 6.

## 4. The browser matrix
Eight cells in `docs/spike-runbook.md` (Chrome and Edge × managed and unmanaged × W10 and W11 × the handoff case).

## 5. The security checks per environment

Run from `scripts/security/` (see `scripts/security/README.md`):

| Check | When | On which machine |
|---|---|---|
| `check-listener.sh` | During a live session's connect window | From a third machine on the same network, and from the internet |
| `check-egress-blocks.sh` | During an active session | The user's machine |
| `check-session-keys.sh` | After every session ends | SRV |
| `check-logs-clean.sh` | At the end of every test day | SRV and both Windows machines |

## 6. Edge cases (added in week 5)

Tested by hand on Windows; the automated tests cover their logic but not the system's interaction.

| Case | How it is tested | Expected |
|---|---|---|
| Sleep and wake | Closing the lid for more than two minutes during a live session | The session ends with a clear reason, the work browser closes, and the control channel comes back within seconds of waking (a "backoff shortened" line in the log) |
| A network change | Switching from Wi-Fi to Ethernet during a session | Immediate reconnection with no wait for the exponential backoff |
| An application crash and an abandoned browser | Killing `Josour.exe` from Task Manager and restarting | The abandoned window closes before the application becomes usable, and the marker file is deleted |
| The user's personal browser | Starting personal Chrome first, then killing and restarting the application | The personal browser is **not** closed |
| The access token expiring during a session | The server closing the connection with code 4401 | One refresh and a reconnection, or a clean teardown if that is not possible |
| Clock drift | Setting the machine's clock 45 minutes fast | The session's duration neither shortens nor lengthens, and a warning appears in the log |
| A right-to-left interface | Running in Arabic (the default) and with `--lang en` | The layout is a correct mirror, addresses, device names and numbers read from the left inside Arabic text, and the digits are Western |
| A missing firewall rule | Deleting the "Josour Tunnel" rule and then enabling "available" | A warning on the host page before announcing |

## 7. What is recorded per run
The machine and network code, the system version and build, the application's version, the result of each of the 18
items, and the `connect_diagnostics` output from the server. The results are collected in
`docs/acceptance-checklist.md`.
