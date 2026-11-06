# Changelog

Every release of Josour: the apps, the server and the relay share one version number
([docs/releasing.md](docs/releasing.md)). The installable files of each release are on the
[Releases page](https://github.com/mo-shaker/josour/releases).

## Unreleased

### Android
- **A new look around the qantara mark.** The phone's interface is rebuilt on the new logo
  ([docs/design/mobile-phase1-foundation.html](docs/design/mobile-phase1-foundation.html), mockups in
  [mobile-phase2-mockups.html](docs/design/mobile-phase2-mockups.html)): colours derived from the logo's teal and ink with
  light and dark themes, Readex Pro (the wordmark's own typeface) for Arabic and English instead of Noto Naskh Arabic UI,
  the lock-up on the sign-in screen, and a new launcher icon (adaptive, with a themed icon on Android 13+), notification
  icon and splash screen.
- **The Share tab answers one question first**: can a request reach the phone right now, and if not, what to do. One status
  (off, getting ready, ready, unreachable, no internet, needs attention, in session, ended) with the one button that fixes
  it; a short reconnect no longer reads as "requests can't reach you".
- **Consent screens are fairer.** Accept and Reject on a request are the same size and weight and come after "What
  accepting means"; an expired request says so instead of vanishing; letting a contact connect without asking now asks
  for confirmation first; signing out asks first and names what stops.
- Text follows Android's font size setting (up to 200%, non-linearly as Android does), every control has an accessible
  name, the session notification counts the time left down by itself, and the theme follows the phone's light or dark
  setting while the app runs. Sign-in shows why the server signed the phone out.
- Some shared wording changed meaning-for-meaning on Windows and macOS too: "the person who runs your Josour server"
  instead of "your administrator", "this server's list of allowed sites" instead of "the company allow-list", "From"
  instead of "Requester", and "N seconds left to answer".

### Server
- **The same-network warning now works over IPv6.** Each device on an IPv6 network has its own address, so
  `same_public_ip` (in the host list and in `session.created`) now compares the `/64` network instead of the exact
  address; an IPv4 address seen through a dual-stack socket (`::ffff:…`) is compared as plain IPv4. A device that
  reaches the server over IPv4 and another over IPv6 still cannot be compared and count as different.

## 2.5.0 - 2026-11-03

### Windows and macOS
- **A warning when the host is on your own network.** Browsing through a device that has your own public address (the
  same Wi-Fi, or the same mobile carrier's shared address) works, but sites still see your address, which looks like
  "the IP did not change". The session panel now says so while such a session runs, the Contacts tab marks a contact
  whose shared device is on your network, and "Use their connection" picks one of the contact's other devices first.

### Server
- Each entry of the host list (`hosts.snapshot`, `hosts.update`, `GET /hosts`) carries `same_public_ip`, computed for
  the device that receives the list; neither address is sent. Older apps ignore it.

## 2.4.0 - 2026-11-03

### Android
- **Contact invitations reach the phone.** A new Contacts page (beside "Share", at the bottom of the screen) lists the
  invitations waiting for an answer, with Accept and Decline, then the contacts themselves (online or not, "can use my
  connection without asking", Remove), inviting someone by email and the invitations you sent. A new invitation also
  arrives as a notification that opens that page, and the host page and the Contacts tab show how many are waiting.
- **Requests no longer get lost on a connection that died quietly.** Opening the app, a network change or the new
  "Reconnect now" button now checks the connection with a ping and reconnects within seconds when nothing answers,
  instead of waiting up to a minute. The host page says plainly whether a request can reach the phone right now
  ("Ready…", or why not), and warns when notifications are off.

### Server
- **A request rings its host again when the host reconnects.** A request was sent once, to whatever connection the
  host's device had at that moment; one lost on a connection that had just died stayed pending until it expired, and
  blocked new requests to that host meanwhile. The server now sends every request still waiting on a device to its new
  connection. Servers need this release (`deploy/install.sh`) for it.

## 2.3.0 - 2026-11-03

### Android
- **Arabic is readable on the phone.** Every Arabic letter was drawn as an empty box, because the app did not reach the
  phone's own Arabic fonts. The app now carries its own Arabic font (Noto Naskh Arabic UI, SIL Open Font License) and
  uses it for any letter the default font lacks.
- **The phone starts in English**, whatever language the phone itself is set to, and a button at the top of the sign-in
  and host pages switches to Arabic (or back) at once. The choice is saved as the `language` setting and kept across
  restarts. The desktop apps are unchanged.

## 2.2.0 - 2026-11-02

### Android (first version: the phone as a host)
- **An Android phone can share its connection.** Signed in with the same account as on a computer, the phone turns
  "Available" on and contacts on Windows or macOS can ask to browse through it; the sites they visit see the phone's
  public address. It works the same on Wi-Fi and on mobile data, and moving from one to the other reconnects at once.
  The phone answers with the same consent screen as the desktop (who, which device, how long, what they may reach),
  accepts a trusted contact without asking, and shows the time left and the data used, with "End the session now".
- A foreground service with a permanent notification keeps the phone reachable with the screen off; a request arrives
  as a notification that opens the consent screen (it has no Accept button of its own). On mobile data the app says the
  guest's browsing counts against the data plan, and it warns when a VPN would hide the phone's own address.
- The guest's traffic is held to the same egress policy as on the desktop (the same code), and the phone's Wi-Fi router
  and its other networks are blocked using what Android reports, since .NET cannot read the routing table there.
- The phone is a host only in this version: it does not take offers and cannot borrow a connection.
- The device secret and refresh token are encrypted with a key held in the Android Keystore.
- **Download link:** `releases/latest/download/Josour-android.apk`, in the README with the other apps. Each release
  now carries the APK, signed with one release key kept in the repository secrets
  ([docs/releasing.md](docs/releasing.md#the-android-signing-key)).
- Build with `scripts/android-toolchain.sh` (once) and `scripts/android-build.sh`; the APK is
  `client/publish/Josour-android.apk`. The desktop control channel now takes its `hello.capabilities` from its options.

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
