# Installing and using Josour

A practical guide from nothing: a server on the internet, the app on two machines, and a session that proves the
sites see the host's address rather than the guest's.

**There is a Windows app and a macOS app**, and they are the same application — one codebase, one interface, both
roles on both systems ([ADR-0013](decisions/0013-avalonia-and-macos.md)). What differs between them is only how you
get past the operating system's warning about software nobody paid to sign.

**Nobody runs a public relay.** Whoever wants Josour runs their own server; this guide is the way.

> **Read [the host's responsibility in the README](../README.md) before you run this for anyone.** You are about to
> make your home's address the exit for someone else's browsing.

## What you are about to build

```
  The guest's machine                  Your server                  The host's machine
 ┌───────────────────┐          ┌───────────────────────┐          ┌───────────────────┐
 │ Josour app        │◄────────►│ API: who may ask whom │◄────────►│ Josour app        │
 │ + a work browser  │   WSS    │ relay: opaque bytes   │   WSS    │                   │
 └─────────┬─────────┘          └───────────┬───────────┘          └─────────┬─────────┘
           │                                │                                │
           └───────── the tunnel: TLS end to end, through the relay ─────────┘
                 the server cannot read a URL, a domain or a byte of it
```

The server coordinates; it never sees browsing content. The sites see the **host's** IP address.

## Before you start

| You need | Why |
|---|---|
| A small Linux VPS (1 vCPU, 2 GB, Ubuntu 24.04) | The server. 4–6 dollars a month; its measured capacity is 500 concurrent clients ([load test](load-test-week5.md)) |
| A domain name you control | For `rb.example.com` and its TLS certificate |
| Two machines, Windows or macOS, ideally on **two different networks** | One machine works but proves nothing: the whole point is that traffic leaves from the *other* machine's address |
| About 30 minutes | Most of it waiting for DNS and for a first build |

**Honest limits before you invest the time:**

- **No code-signing certificate** ([ADR-0011](decisions/0011-no-code-signing-certificate.md)), so Windows and macOS
  both warn on first launch. Getting past it is one step, once per machine, explained below.
- **A real session between a Mac and a Windows machine has not been tried yet** — it needs two machines and both
  have not been in the same pair. Every piece of it is exercised separately ([macos-port.md](macos-port.md)). A
  Windows↔Windows session has run in earnest since 2026-09-09.
- **No support.** If you get stuck, [part 6](#6-if-it-does-not-work) covers the known failures and the source is open.

---

## 1. The server

### 1.1 The DNS record, first

Create an `A` record pointing `rb.example.com` at the server's address, and wait for it:

```bash
dig +short rb.example.com
```

The server's address must appear. **Do not continue before it does**: Caddy asks Let's Encrypt for a certificate on
its first run and will fail without the record.

### 1.2 Prepare the machine (once)

Over SSH:

```bash
apt update && apt -y upgrade
apt -y install ufw fail2ban unattended-upgrades ca-certificates curl git
ufw default deny incoming && ufw default allow outgoing
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp
ufw --force enable
dpkg-reconfigure -plow unattended-upgrades
curl -fsSL https://get.docker.com | sh
```

Three ports, and the database is never exposed to the internet.

### 1.3 Fetch the code and configure it

```bash
git clone https://github.com/<your-account>/routebridge.git /opt/routebridge
cd /opt/routebridge/deploy
cp .env.example .env
nano .env
```

You **must** set these four:

| Variable | Value |
|---|---|
| `DOMAIN` | `rb.example.com` |
| `ACME_EMAIL` | Your email (certificate-expiry notices) |
| `POSTGRES_PASSWORD` | `openssl rand -base64 32` |
| `JWT_SECRET` | `openssl rand -base64 48` — at least 32 bytes, or the server refuses to start |

Leave the rest. **Do not commit `.env`.** Changing `JWT_SECRET` later signs everybody out at once.

### 1.4 Start it

```bash
docker compose up -d --build
docker compose logs -f api        # wait for "Application startup complete", then Ctrl+C
curl -s https://rb.example.com/healthz
```

Expected: `{"status":"ok","product":"josour","version":"0.1.0"}`

> If it fails, `docker compose logs caddy` usually says why. The two commonest causes: the DNS record has not
> propagated, or port 80 is closed (Let's Encrypt needs it to validate).

### 1.5 The relay — this is what makes a session work with no router configuration

[ADR-0009](decisions/0009-relay-default.md): the relay is the default transport, and a direct connection is an
upgrade tried in parallel that wins when it succeeds. Without a relay a session works **only** when one of the two
machines is reachable from the internet — which is not the case between two mobile networks, the case that failed
twice before it was built.

Generate one secret and use it in two places:

```bash
openssl rand -base64 48
```

```bash
cd /opt/routebridge/deploy
cp .env.relay.example .env.relay && nano .env.relay      # RELAY_SECRET = the secret
                                                          # RELAY_PUBLIC_PORT=8443 if it shares this server
docker compose --env-file .env.relay -f docker-compose.relay.yml up -d --build
ufw allow 8443/tcp
```

Then tell the API about it — add to `deploy/.env`:

```bash
RELAY_HOST=rb.example.com
RELAY_PORT=8443
RELAY_SECRET=<the same secret>
```

and redeploy: `docker compose up -d --build`.

> **Half a configuration stops the server at startup, deliberately.** An address with no secret issues no token; a
> secret with no address is never sent. Either would make every session fall silently back to direct — the very
> failure the relay exists to remove.

> **Where you put it matters more than how big it is.** The relay sits on the path between the two machines. Egypt ↔
> Saudi Arabia via Jeddah is about 40 ms; via Europe about 150. Its own software cost is below a millisecond
> ([measured](performance-relay.md)), so geography is all the user feels.

Check from outside:

```bash
curl -s https://rb.example.com/healthz
timeout 5 bash -c "</dev/tcp/rb.example.com/8443" && echo "the relay port is open"
```

### 1.6 The first administrator

```bash
docker compose exec api python manage.py create-admin \
  --email admin@example.com --password 'a-strong-password' --display-name "Admin"
```

That is the only account you need from the command line. **Everyone else can be created from inside the app** —
see [part 5](#5-managing-users).

### 1.7 A first backup

```bash
docker compose exec backup /usr/local/bin/backup.sh
ls -la backups/
```

Backups run daily after that. **Copy `backups/` off the server regularly**; a copy on the same disk is not a
disaster plan. The restore script is `./backup/restore.sh`, and it has actually been exercised
([runbook.md](runbook.md)).

---

## 2. The apps

Both apps are the same program: the same screens, both roles, the same server. Pick the section for each machine.

### 2.1 The Windows app

**Get it.** The easiest route is a CI build: open your repository → **Actions** → the last successful `ci-client`
run → download the `josour-app-unsigned` artefact → unpack it somewhere permanent such as `C:\Josour`.

Or build it yourself (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
git clone https://github.com/<your-account>/routebridge.git
cd routebridge
powershell -ExecutionPolicy Bypass -File scripts\publish-exe.ps1
```

That produces **one file**, `client\publish\exe\Josour.exe`, and prints its SHA-256. Do not use `dotnet publish`
directly: the native libraries are not bundled by default, so you get a file that works on the build machine and
dies silently on any other. The script checks for that and refuses an incomplete output.

**Verify it, then get past SmartScreen.** The file is unsigned, and that is a decision rather than an omission
([ADR-0011](decisions/0011-no-code-signing-certificate.md)). A signature proves a file came from someone who bought
a certificate; **a hash proves it is this exact file**:

```powershell
Get-FileHash C:\Josour\Josour.exe -Algorithm SHA256
```

Match it against what the build printed, **over a call or a direct message — not the channel the file arrived on**.
A mismatch means a different file: delete it.

Then "Windows protected your PC" appears once per machine: **More info** → **Run anyway**.

**Optional.** A firewall rule speeds up the direct path when both machines are on one network or behind a router
with UPnP. It is not required — with the relay neither side accepts an inbound connection. From an elevated
PowerShell:

```powershell
netsh advfirewall firewall add rule name="Josour Tunnel" dir=in action=allow program="C:\Josour\Josour.exe" enable=yes profile=domain,private,public protocol=TCP
```

The name must be exactly `Josour Tunnel`: the app checks for a rule by that name and warns if it is missing.

### 2.2 The macOS app

**Build it.** CI compiles the Mac app and runs its tests, but it does not publish a `.app` artefact, so you build it
on a Mac (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```bash
git clone https://github.com/<your-account>/routebridge.git
cd routebridge
scripts/publish-app.sh --dmg                 # this Mac's architecture (Apple silicon or Intel)
scripts/publish-app.sh --arch x64 --dmg      # for an Intel Mac from an Apple-silicon one
```

That produces `client/publish/mac/Josour.app` and `client/publish/mac/Josour-<version>-<arch>.dmg`. Both are
**self-contained**: the .NET runtime travels inside, so the person you hand it to installs nothing first.

The script does not only build — it checks that the bundle contains the two native libraries Avalonia needs to draw,
and then **runs the executable**, because a bundle can assemble cleanly and still die before it opens a window. That
is how it shipped broken the first time.

**Install it.** Open the `.dmg` and drag Josour onto Applications, as with any Mac app.

**The first launch, once per machine.** The app is unsigned, so Gatekeeper is stricter than SmartScreen:

> **Right-click the app → Open → Open.**
>
> Double-clicking it first only says it cannot be opened, with no way forward in that dialog. Right-click → Open is
> the one path macOS gives you, and you need it once; after that it opens normally.

**Optional.** "Start at login" in the app's settings writes a LaunchAgent at
`~/Library/LaunchAgents/com.josour.client.plist`; nothing to configure by hand.

### 2.3 What differs between the two, and what does not

| | Windows | macOS |
|---|---|---|
| Both roles (host and guest) | Yes | Yes |
| The screens | The same | The same |
| First-launch warning | SmartScreen: More info → Run anyway | Gatekeeper: right-click → Open → Open |
| Notification buttons | "Accept" and "Reject" on the toast | No buttons — the request window is the way to answer |
| Secret storage | DPAPI | A file with `0600` permissions ([why](decisions/0013-avalonia-and-macos.md)) |
| Start at login | A `Run` registry entry | A LaunchAgent |
| Firewall rule | Optional, speeds up direct | Not applicable |

The notification difference is worth knowing but changes nothing in practice: the request window is the primary path
on both systems anyway, because Focus Assist on Windows drops notifications too.

---

## 3. First run

Start the app. The interface is **in Arabic by default**, with right-to-left layout. For English:

```
Josour.exe --lang en                                  # Windows
/Applications/Josour.app/Contents/MacOS/Josour --lang en   # macOS, from a terminal
```

There is also a language setting inside the app (it applies on the next start).

The guided first run asks for three things:

1. **The server's address** — `https://rb.example.com`. It is actually checked before you can continue: the app asks
   `/healthz` and looks for the product marker, so a wrong address fails here rather than later as "wrong password".
2. **Sign in** — the account you created, or one an administrator made for you.
3. **A readiness summary** — the firewall rule's state, and a warning if a VPN holds this machine's egress route.

> **If you use a VPN on the host machine, turn it off.** The sites will see the VPN's address, not the machine's,
> which defeats the point. The app warns you; it does not stop you.

Closing the main window leaves the app running in the tray (Windows) or the menu bar (macOS). It does not exit.

---

## 4. Running a session

### 4.1 On the host's machine — the one sharing its connection

Turn on **"available to receive requests"**. That is all. The machine now appears in the guest's list.

### 4.2 On the guest's machine — the one borrowing the connection

The host appears in the list. Select it, choose a duration — **15, 30, 60 or 120 minutes** (capped by the server's
`max_session_minutes`) — and send the request.

### 4.3 Back on the host: the consent

A notification and a window appear showing who is asking, from which device, for how long, **the browsing scope**,
and the warning that the sites will see your IP address. You have 60 seconds.

Read it before you accept. With the site list off (the default), the window says plainly that the guest will be able
to browse **any site** over your connection.

**Accept.**

> **"Accept from this guest automatically" (optional).** Ticking it while accepting adds this guest *on this device*
> to a trust list, so their next request is accepted with no window
> ([ADR-0012](decisions/0012-auto-accept-trusted-guests.md)). The consent is still yours — given in advance, capped
> by the duration you just granted, expiring on its own after a week by default, and withdrawable from Settings at
> any moment. You still get a notification each time it happens, and you can end the session from it.

### 4.4 On the guest: the proof

A separate **work browser** opens on the check page. Go to `https://api.ipify.org`.

**That is the whole test.** The **host machine's** IP address must appear, not yours. Open your ordinary browser on
the same address at the same moment — your own address appears there. Two different numbers is the product.

While the session runs:

- Your other applications are unaffected. Teams, Outlook and your normal browser stay on your own connection with
  your own address. The local proxy serves the work browser alone, and it checks which process owns each connection.
- The host's local network, their router's page and their `localhost` services stay out of reach. That check is not
  the site list — it is applied after every name is resolved and before any socket is opened, and it is not
  negotiable.
- Either side can end the session at any moment. The work browser closes with it.
- The session ends by itself when the duration runs out.

---

## 5. Managing users

**Inside the app, for anyone whose role is `admin`.** Sign in as the administrator and open the panel: the button in
the main window's header (it only appears for administrators) or the tray/menu-bar menu.

From there you can:

| | |
|---|---|
| **List** the accounts | With their state and when each was last seen |
| **Create** an account | Email, display name, password, role |
| **Disable / enable** one | Disabling takes effect at once: it signs the account out and cuts any live session |
| **Unlock** one | After ten failed sign-ins an account locks for 15 minutes; this clears it |
| **Set a password** | The account's devices sign in again afterwards |

**There is no delete, and that is deliberate.** A session row points at both of its parties with
`ON DELETE CASCADE`, so deleting an account would erase the record of every session it was part of — including the
other person's half. Disabling removes someone just as completely and leaves everyone else's history intact.

**There is no web panel**, on purpose: an administrative panel on the internet would be the highest-value target in
the system, and this one exposes nothing.

**The command line still works**, for a first administrator or a machine with no interface:

```bash
docker compose exec api python manage.py create-admin --email a@b.c --password '…' --display-name "Name"
docker compose exec api python manage.py create-user  --email a@b.c --password '…' --display-name "Name"
docker compose exec api python manage.py list-sessions
docker compose exec api python manage.py end-session <session-id>
docker compose exec api python manage.py add-domain example.com
docker compose exec api python manage.py list-domains
```

### 5.1 The site list — optional, and off by default

Since [ADR-0010](decisions/0010-route-all-through-host.md) **everything** the work browser asks for goes through the
host, and `enforce_allowlist` is off. You do not need to add a single domain to try it.

The list stays available as a control if you want to restrict a deployment to specific destinations: add domains
with `add-domain`, then turn it on with `PATCH /admin/settings` and `{"enforce_allowlist": true}`.

> Turning the list off changes **one** of the eight egress-policy steps. Blocking internal addresses at the host, the
> refusal of address literals, strict name normalisation and the limit to ports 80 and 443 all stay exactly as they
> are.

---

## 6. If it does not work

The log names the failure. On Windows:

```powershell
Get-Content "$env:LOCALAPPDATA\Josour\logs\app-*.log" | Select-String "Tunnel connected|connect_failed|No probe page" | Select-Object -Last 5
```

On macOS:

```bash
grep -E "Tunnel connected|connect_failed|No probe page" ~/Library/Application\ Support/Josour/logs/app-*.log | tail -5
```

| What you read | What it means | The step |
|---|---|---|
| `winner="Relay"` | The tunnel stood up over the relay | Sound |
| `winner="Lan"` or `"Upnp"` | Direct won — faster, but the two networks are not different enough to prove the address changed | Put the machines on two networks |
| `connect_failed` with `Relay available` above it | The relay is announced and did not succeed | Check its port is open from outside, and that `RELAY_SECRET` **matches** in both files |
| `connect_failed` with no `Relay available` | The server is not sending the relay at all | `RELAY_HOST` or `RELAY_SECRET` is missing from `deploy/.env` |
| `No probe page … (accepted=N rejected_by_owner=N)` | The work browser reaches the proxy and the proxy refuses it | A defect in the app — send that line |
| `No probe page … (nothing ever connected)` | The browser is not using the proxy | On Windows, [`scripts/diagnose-work-browser.ps1`](../scripts/diagnose-work-browser.ps1) settles it |

**Sign-in fails on macOS with an OSStatus error.** That was the Keychain, and it is gone — secrets now live in a
file with `0600` permissions. Make sure you are running a build from 2026-09-13 or later.

**The app will not open on macOS at all.** Right-click → Open, not double-click. See [2.2](#22-the-macos-app).

**Nothing opens on Windows and nothing is logged.** That is the signature of an incomplete publish: `dotnet publish`
used directly instead of `scripts\publish-exe.ps1`. Rebuild with the script, which refuses an output that is not
shareable.

**To see whether a direct path was ever possible** on a given network:

```powershell
dotnet run --project client\tools\Josour.Spike -c Release -- gather --port 40000 --public-ip <your public address>
```

`upnp_found: false` with `ipv6_global: false` and a lone `public` candidate means direct is impossible from that
network — which is exactly what the relay carries.

**A headless end-to-end check**, if you want to separate a tunnel failure from an interface failure, is in
[spike-runbook.md](spike-runbook.md): the `session` command drives the same stack with no UI and prints one JSON
line per event.

---

## Verification summary

| # | Check | Expected |
|---|---|---|
| 1 | `curl https://rb.example.com/healthz` | `{"status":"ok","product":"josour",…}` |
| 2 | The relay's port from outside | Open |
| 3 | Signing in from both machines | Succeeds |
| 4 | The host appears in the guest's list | Within seconds |
| 5 | The request window on the host | Name, device, duration, **"browsing scope"**, and the warning that sites will see your address |
| 6 | `api.ipify.org` in the work browser | **The host's address** |
| 7 | The same address in your ordinary browser | **Your own address** |
| 8 | Teams/Outlook during the session | Work on your address, unaffected |
| 9 | Disconnecting from either side | Closes the work browser within seconds |
| 10 | The duration running out | The session ends on its own |

The full 18-item list is in [acceptance-checklist.md](acceptance-checklist.md).

---

## References

| File | Contents |
|---|---|
| [runbook.md](runbook.md) | Running the server: upgrading, backup and restore, monitoring, automatic deployment |
| [macos-port.md](macos-port.md) | What the Mac build does differently, and what is still unproven there |
| [spike-runbook.md](spike-runbook.md) | The headless tool in detail: every command and every JSON event |
| [acceptance-checklist.md](acceptance-checklist.md) | The 18 success criteria and the security checks |
| [test-matrix.md](test-matrix.md) | The device, network and edge-case matrix |
| [load-test-week5.md](load-test-week5.md) | The server's measured capacity |
| [api.md](api.md) · [ws-protocol.md](ws-protocol.md) · [protocol.md](protocol.md) | The frozen contracts |
| [client/installer/README.md](../client/installer/README.md) | Building the signed Windows installer, if a certificate is ever bought |
