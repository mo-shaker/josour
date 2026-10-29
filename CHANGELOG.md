# Changelog

Every release of Josour: the apps, the server and the relay share one version number
([docs/releasing.md](docs/releasing.md)). The installable files of each release are on the
[Releases page](https://github.com/mo-shaker/josour/releases).

## Unreleased

## 2.1.1 - 2026-10-29

### Fixed
- **The Mac app no longer says it is "damaged and can't be opened"** after it is downloaded. The bundle is now signed
  as a whole (ad hoc, still without an Apple developer certificate), so macOS shows its usual first-launch question,
  which is allowed once with **Open Anyway** (or right-click → **Open**). The build checks the signature inside the
  `.dmg` before it is published.

## 2.1.0 - 2026-10-28

### Install and release
- **A server installs with one command** on a fresh Ubuntu or Debian machine: `deploy/install.sh` asks for the name,
  an email and the administrator's password, and sets up the firewall, Docker, the certificate, the secrets, the relay
  on port 8443, the administrator and the daily backups. Running it again updates the server to the newest release.
  Step-by-step guides for people who have never run a server, in English and Arabic:
  [docs/install-server.md](docs/install-server.md), [docs/install-server.ar.md](docs/install-server.ar.md).
- **Direct download links that always give the newest app** (`releases/latest/download/Josour-win-x64.exe` and the two
  macOS images), a `SHA256SUMS.txt` with every release, and release notes taken from this file.
- `scripts/release.sh X.Y.Z` sets one version everywhere, dates this changelog, commits and tags; the release
  workflow refuses to rebuild a released version from a different commit ([docs/releasing.md](docs/releasing.md)).

### Security
- The host's own network is now out of reach over **IPv6** too: the guest could reach a device on the host's network
  through its global IPv6 address. The host's on-link networks are blocked, not only its exact addresses.
- A hostile plain-`http` site can no longer receive the browser's next request for **another** site on the same proxy
  connection: every reply is relayed with `Connection: close`.
- Addresses written as names (`8.8.8.8.`, `0x7f.0.0.1.`, full-width digits) are refused as address literals; NAT64
  local-use, SIIT, site-local and documentation ranges are blocked; peer candidates the host would dial are filtered.
- The defaults fail closed: no allow-list means no direct path, and a tunnel route without a tunnel is refused.

### Server security
- **The dashboard's two-factor code can no longer be guessed without limit:** five wrong codes end the dashboard
  session, and wrong codes count toward the account lockout like wrong passwords. Turning on
  `ADMIN_WEB_REQUIRE_TOTP` now applies to sessions that already exist (migration 0003).
- WebSocket messages are size-limited (1 MiB, 128 KiB for the first one) before they are read, and their lists are
  bounded; a client that stops reading can no longer stall the others.
- Two answers to the same request or offer at the same moment (accept and cancel, accept and expiry) settle on one;
  two refreshes of one sign-in token are caught as token reuse; two administrators demoting each other can no longer
  leave the server with none.
- Creating an account, setting a password, re-enabling and unlocking an account are now in the security log, and
  every dashboard action is recorded with `via: "web"`.
- A network outside `ADMIN_WEB_ALLOWED_CIDRS` gets the API's ordinary 404 on every `/admin` path, static files included.
- `POST /diagnostics` is rate-limited (`DIAGNOSTICS_RATE_LIMIT_PER_HOUR`, 60), and a duplicate write answers 409 rather
  than 500.

### Fixes
- **A host could fail every session after the first** until someone pressed *Dismiss*: the next request was shown but
  its session never started. A host in the tray now takes the next request at once.
- The system proxy is put back more reliably: the crash marker is written to disk before it counts, an unreadable
  marker is no longer mistaken for "nothing to restore", a guardian can no longer undo a newer session, and on macOS a
  network service that cannot be read keeps the marker instead of being skipped.
- A sign-in refresh cut short by a timeout could lose the new token and sign the user out; two refreshes of one token
  at start-up could do the same.
- A request the app had given up on could still start a session if the server's answer arrived late.
- Quitting with Cmd+Q on a Mac now ends the session properly (the other side sees why it ended).

## 2.0.0 - 2026-10-21

- **Any browser goes through the host.** During a session the guest's app sets the system proxy, so Chrome, Edge,
  Safari or Firefox all leave from the host's address, and puts the previous setting back afterwards, after a crash
  too. The separate "work browser" is gone ([ADR-0015](docs/decisions/0015-system-proxy-instead-of-work-browser.md)).
- **Contacts.** Hosts are visible only to contacts, added by email; either side can ask for the other's connection
  or offer its own, and "use my connection without asking" is held by the server per contact
  ([ADR-0014](docs/decisions/0014-contacts-and-server-held-trust.md)).
- **Registration from the app**, with administrator approval by default
  ([ADR-0017](docs/decisions/0017-self-registration-with-approval.md)).
- **A web dashboard** at `https://<your server>/admin`, in Arabic and English, with optional two-factor sign-in
  ([ADR-0016](docs/decisions/0016-web-admin-dashboard.md)).
- Ports other than 80 and 443 are refused instead of leaving the guest directly; plain `http` goes through the host.
- `min_client_version`: the server can tell apps older than a given version to update.

## 1.x - September 2026

The first releases: the end-to-end encrypted tunnel between the two machines, the relay that makes a session work
with no router configuration ([ADR-0009](docs/decisions/0009-relay-default.md)), one Avalonia app for Windows and
macOS ([ADR-0013](docs/decisions/0013-avalonia-and-macos.md)), the Arabic interface, and the control server with
rate limiting and suspicious-device detection.
