# Installing and using Josour

A practical guide from nothing: a server on the internet, the app on two machines, and a session that proves the
sites see the host's address rather than the guest's. It also covers the case of **wiping an existing server and
installing it again** — what to keep before you format, and how to bring it back.

**There is a Windows app and a macOS app**, and they are the same application — one codebase, one interface, both
roles on both systems ([ADR-0013](decisions/0013-avalonia-and-macos.md)). What differs between them is only how you
get past the operating system's warning about software nobody paid to sign.

**Nobody runs a public server.** Whoever wants Josour runs their own; this guide is the way.

> **Read [the host's responsibility in the README](../README.md) before you run this for anyone.** You are about to
> make your home's address the exit for someone else's browsing.

> **The quick way.** [`deploy/install.sh`](../deploy/install.sh) performs parts 2 and 3 of this guide by itself on a
> fresh Ubuntu or Debian server — asking only for the name, an email and the administrator's password — and running
> it again updates the server. [install-server.md](install-server.md) ([بالعربية](install-server.ar.md)) walks through
> it for someone who has never run a server. This guide is the manual equivalent, with the reasons behind each step.

Throughout, `josour.example.com` stands for your server's name and `you@example.com` for your email. Replace both.
Every server command runs as `root` (on a machine where you log in as another user, run `sudo -i` first).

## What you are about to build

```
  The guest's machine                     Your server                       The host's machine
 ┌──────────────────────┐   HTTPS + WSS   ┌──────────────────────────┐   HTTPS + WSS   ┌──────────────────────┐
 │ Josour app           │◄───── 443 ─────►│ Caddy → API: who may ask │◄───── 443 ─────►│ Josour app           │
 │ any browser, through │                 │ whom, accounts, contacts │                 │                      │
 │ the system proxy     │                 ├──────────────────────────┤                 │                      │
 └──────────┬───────────┘    plain TCP    │ relay: pairs the two     │    plain TCP    └──────────┬───────────┘
            └────────────── 8443 or 443 ─►│ sides, pumps opaque bytes│◄─ 8443 or 443 ───────────┘
                                          └──────────────────────────┘
            └──── the tunnel: TLS end to end between the two apps, inside that TCP stream ────┘
                   the server cannot read a URL, a domain or a byte of what is browsed
```

- The **API** (behind Caddy, which holds the TLS certificate) coordinates: accounts, contacts, requests, consent,
  sessions. The apps keep a WebSocket open to it.
- The **relay** is a separate, optional-but-recommended service. It is plain TCP: the TLS between the two apps
  runs *inside* its stream, so nothing terminates TLS there and it sees ciphertext only. A direct connection
  between the two machines is tried in parallel and wins when it works.
- On the guest, during a session, the app **sets the operating system's proxy**, so *any* browser — Chrome, Edge,
  Safari, Firefox — goes out through the host. When the session ends the app puts the previous proxy setting back.
  There is no separate "work browser" any more ([ADR-0015](decisions/0015-system-proxy-instead-of-work-browser.md)).
- The sites see the **host's** IP address.

## Before you start

| You need | Why |
|---|---|
| A small Linux VPS (1 vCPU, 2 GB, Ubuntu 24.04) | The server. 4–6 dollars a month; its measured capacity is 500 concurrent clients ([load test](load-test-week5.md)) |
| A domain name you control | For `josour.example.com` and its TLS certificate |
| Two machines, Windows or macOS, ideally on **two different networks** | One machine proves nothing: the whole point is that traffic leaves from the *other* machine's address. A phone's hotspot is a fine second network |
| About 45 minutes | Most of it waiting for DNS and for the first build |

**Honest limits before you invest the time:**

- **No code-signing certificate** ([ADR-0011](decisions/0011-no-code-signing-certificate.md)), so Windows and macOS
  both warn on first launch. Getting past it is one step, once per machine, explained in [part 4](#4-the-apps).
- **No support.** If you get stuck, [part 8](#8-if-it-does-not-work) covers the known failures and the source is open.

---

## 1. Before you format an existing server

Skip this part on a brand-new server.

Formatting destroys the database (accounts, contacts, the security log) and the configuration. **Restoring them
afterwards is optional**:

| | Start empty | Restore the old database |
|---|---|---|
| Accounts | Created again (by you, or by people registering from the app) | Come back with their passwords |
| Contacts and "accept without asking" | Invited again, or `seed-contacts` ([part 5](#5-accounts-and-contacts)) | Come back |
| Sign-ins on the machines | Everyone signs in again | Everyone signs in again anyway if the apps are reinstalled clean |
| Effort | None | One command after the first start ([2.6](#26-restore-the-old-database-optional)) |

For a handful of people who are reinstalling their apps anyway, starting empty is perfectly reasonable. Take the
backup regardless: it costs a minute and it is the only way back if you change your mind.

On the **old** server, find where the stack lives (the `CONFIG FILES` column), then go there:

```bash
docker compose ls
cd /opt/routebridge/deploy        # or whatever directory the line above showed, ending in /deploy
```

Take a fresh database dump. This works on every version of the stack, old or new:

```bash
set -a; . ./.env; set +a
docker compose exec -T db pg_dump -U "$POSTGRES_USER" --no-owner --no-privileges "$POSTGRES_DB" \
  | gzip -9 > final-before-format.sql.gz
ls -la final-before-format.sql.gz         # a few kilobytes at least; a few hundred bytes means it is empty
```

Then, **from your own computer**, copy off everything worth keeping (adjust the path and the server name):

```bash
mkdir -p josour-server-backup && cd josour-server-backup
scp root@josour.example.com:/opt/routebridge/deploy/.env                     env-old
scp root@josour.example.com:/opt/routebridge/deploy/.env.relay               env-relay-old   # only if it exists
scp root@josour.example.com:/opt/routebridge/deploy/final-before-format.sql.gz .
scp -r root@josour.example.com:/opt/routebridge/deploy/backups               .               # the daily dumps
```

| File | Why keep it |
|---|---|
| `final-before-format.sql.gz` | The database, as of now. Needed only if you restore |
| `env-old` (`deploy/.env`) | Its `JWT_SECRET` and `RELAY_SECRET` can be reused; it also reminds you of `DOMAIN` and the relay settings |
| `env-relay-old` (`deploy/.env.relay`) | The relay's secret and port, if you ran a relay with that file (on a separate relay host, the old instructions put them in that host's `deploy/.env`) |
| `backups/` | Older daily dumps, in case the newest one is not the one you want |

Not needed: the TLS certificate. Caddy simply asks Let's Encrypt for a new one on the first start (its limit is
five certificates for the same name per week, which only matters if you reinstall again and again; if you do,
also save the `deploy_caddy_data` Docker volume).

Check the dump is readable before you format — the next command prints the first table definitions:

```bash
gunzip -c final-before-format.sql.gz | grep -m 3 'CREATE TABLE'
```

Your computer will complain about a changed host key the first time you SSH to the reinstalled server. That is
expected after a format: `ssh-keygen -R josour.example.com`, then connect again.

---

## 2. The server

### 2.1 The DNS record, first

Create an `A` record pointing `josour.example.com` at the server's IPv4 address, and wait for it:

```bash
dig +short josour.example.com
```

The server's address must appear. **Do not continue before it does**: Caddy asks Let's Encrypt for a certificate on
its first run, over port 80, and fails without the record. If you are reinstalling the same server under the same
name and its address did not change, the record is already there.

> **An `A` record only, no `AAAA`.** With Docker's default network, visitors arriving over IPv6 reach Caddy through
> Docker's userland proxy and appear to the API as the Docker network's gateway (`172.x.0.1`). Sign-in rate limits
> and the dashboard's network restriction would then see one address for all of them.

### 2.2 Prepare the machine (once)

```bash
apt update && apt -y upgrade
apt -y install ufw fail2ban unattended-upgrades ca-certificates curl git
dpkg-reconfigure -plow unattended-upgrades

ufw default deny incoming && ufw default allow outgoing
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp
ufw --force enable

curl -fsSL https://get.docker.com | sh
docker compose version            # Docker Compose version v2.24 or later
```

Use SSH keys rather than passwords (`PasswordAuthentication no` in `/etc/ssh/sshd_config`).

> **How the firewall really works here.** Ports that Docker publishes — Caddy's 80 and 443, and the relay's port if
> you run one — **bypass ufw**: Docker writes its own iptables rules, so `ufw allow` neither opens them nor closes
> them. ufw still protects everything that is not a Docker port (SSH above all). The stack publishes only Caddy and
> the relay; PostgreSQL and the API are never exposed. A **cloud provider's firewall** (Hetzner, AWS security
> groups, Oracle, …) sits outside the machine and *does* apply: open 80, 443 (TCP and UDP) and the relay's port there.

### 2.3 Fetch the code and configure it

```bash
git clone https://github.com/mo-shaker/josour.git /opt/josour
cd /opt/josour/deploy
cp .env.example .env
chmod 600 .env
```

(If the repository is private, clone with a GitHub personal access token or a deploy key.)

Four values are **required**; `docker compose` refuses to start until each one is set. Set them in one go — replace
the name and the email, the two secrets are generated on the spot:

```bash
sed -i "s|^DOMAIN=.*|DOMAIN=josour.example.com|" .env
sed -i "s|^ACME_EMAIL=.*|ACME_EMAIL=you@example.com|" .env
sed -i "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$(openssl rand -hex 32)|" .env
sed -i "s|^JWT_SECRET=.*|JWT_SECRET=$(openssl rand -hex 32)|" .env
grep -E '^(DOMAIN|ACME_EMAIL|POSTGRES_PASSWORD|JWT_SECRET)=' .env
```

| Variable | What it is |
|---|---|
| `DOMAIN` | The server's name; the dashboard is at `https://DOMAIN/admin` |
| `ACME_EMAIL` | Let's Encrypt's certificate-expiry notices |
| `POSTGRES_PASSWORD` | Stored by PostgreSQL **when its volume is created, on the first start**. Changing it in `.env` later does not change the database's password — it only locks the API out. Set it once, now |
| `JWT_SECRET` | Signs the access tokens; at least 32 characters or the API refuses to start |

Everything else in `.env` is optional and documented in the file itself; leave it for now. If you are restoring and
want to keep the old `JWT_SECRET`, paste it from `env-old` instead of generating one (not required: a new one only
invalidates access tokens that live 15 minutes).

**Do not commit `.env`**, and keep a copy off the server — it is what [part 1](#1-before-you-format-an-existing-server)
told you to save.

### 2.4 Start it

```bash
docker compose up -d --build
docker compose ps                  # wait until api shows "(healthy)": about a minute after the build
curl -s https://josour.example.com/healthz
```

Expected: `{"status":"ok","product":"josour","version":"2.0.0"}`

The first start builds the API image (a few minutes on a small VPS), creates the database and applies the schema
(`alembic upgrade head` runs on every start of `api`). `docker compose logs -f api` shows it: wait for
`Application startup complete`, then Ctrl+C.

> If the `curl` fails, `docker compose logs caddy` usually says why. The commonest causes: the DNS record has not
> propagated, or port 80 is closed in the provider's firewall (Let's Encrypt validates over it).

Always run `docker compose` from `/opt/josour/deploy`, with no `-p`: the backup and security scripts expect the
default project name.

### 2.5 The first administrator

```bash
docker compose exec api python manage.py create-admin --email you@example.com --display-name "Your Name"
```

It asks for the password (at least 8 characters; nothing is shown as you type). This is the one account that has to
come from the command line. **Skip it if you are about to restore the old database**: your old accounts come back
with it, and the restore would erase this one anyway.

### 2.6 Restore the old database (optional)

Only if you chose to in [part 1](#1-before-you-format-an-existing-server). From your computer:

```bash
scp final-before-format.sql.gz root@josour.example.com:/opt/josour/deploy/backups/
```

On the server:

```bash
cd /opt/josour/deploy
./backup/restore.sh backups/final-before-format.sql.gz
docker compose ps                                       # api healthy again
docker compose exec api python manage.py seed-contacts --all-active    # see below
```

The script stops `api`, recreates the database, imports the dump and starts `api` again — which applies any newer
schema migrations on start, so a dump from an older version of the server is brought up to date by itself. It works
whatever `POSTGRES_USER` the old server used (the dumps carry no ownership).

`seed-contacts --all-active` is for a dump from a server **older than contacts** (before October 2026), where every
host was visible to every guest: it makes every active account a contact of every other, so nobody loses a host they
could use before. It is safe to run on any database and to run twice; skip it if you would rather people invite
each other.

### 2.7 The dashboard, and locking it down

Open `https://josour.example.com/admin` ([ADR-0016](decisions/0016-web-admin-dashboard.md)) and sign in with the
administrator's email and password. The dashboard is
**in Arabic by default**; the language link in its header switches to English (and back).

What is there: **Overview** · **Users** (create, disable, unlock, set a password, delete, revoke a device, remove a
contact) · **Registrations** (approve or reject) · **Invitations** · **Sessions** (end one) · **Security log** ·
**Diagnostics** · **Settings** · **Allow-list** · **My account** (your password, two-factor authentication).

Then, in this order:

1. **Two-factor authentication.** *My account* → set up an authenticator app: scan the QR code (or type the key) into
   any TOTP app — Google Authenticator, Microsoft Authenticator, 1Password, Bitwarden — and confirm with a code. To
   make it mandatory for every administrator, set `ADMIN_WEB_REQUIRE_TOTP=true` in `.env` and apply it (below). An
   administrator who has not enrolled yet is sent to enrol right after the password.

2. **Who may reach the dashboard (optional).** `ADMIN_WEB_ALLOWED_CIDRS` takes comma-separated networks, e.g.
   `203.0.113.7/32` for one home address or your VPN's range; anyone else gets a plain 404 on `/admin`, while the
   app keeps working from everywhere. Only use it with an address that does not change — a home connection that
   gets a new address locks you out until you edit `.env` on the server. The Caddyfile has a commented-out block
   that does the same at the edge.

3. **Who may create an account** — *Settings* → *Registration from the app*
   ([ADR-0017](decisions/0017-self-registration-with-approval.md)):

   | Mode | What happens | When |
   |---|---|---|
   | **With administrator approval** (the default) | Anyone with the app can register; the account waits under *Registrations* and cannot sign in until an administrator approves it | People should sign themselves up, and you vet them |
   | **Closed** | The app hides "Create account". Accounts come only from you: *Users* → new, or `manage.py create-user` | A private group where you create every account yourself — the tightest choice |
   | **Open** | A new account can sign in at once, with no approval. It cannot hide whether an email already has an account | Not for a server on the internet |

4. **The oldest app accepted** — *Settings* → *Oldest app version accepted*. Once everyone runs the 2.0 apps, set it
   to `2.0.0`: an older app is then told to update instead of being served. Leave it empty until then.

**Applying a change to `.env`:** `docker compose up -d` — it recreates whatever the change affects.
`docker compose restart` does **not** re-read `.env`.

### 2.8 A first backup

```bash
docker compose exec backup /usr/local/bin/backup.sh
ls -la backups/
```

The `backup` service also writes one every 24 hours (and one each time it starts) as
`backups/josour-<UTC time>.sql.gz`, deleting those older than `BACKUP_RETENTION_DAYS` (14). **Copy `backups/` off
the server regularly**; a copy on the same disk is not a disaster plan ([part 7](#7-running-it)).

---

## 3. The relay — what makes a session work with no router configuration

[ADR-0009](decisions/0009-relay-default.md): the relay is the default transport, and a direct connection is an
upgrade tried in parallel that wins when it succeeds. Without a relay a session works **only** when one of the two
machines is reachable from the internet — which is not the case between two mobile networks, or two homes behind
carrier-grade NAT.

It is a **separate Compose stack** with **its own settings file, `.env.relay`**, and **its own project name,
`josour-relay`**. All three matter, and every relay command below passes them:

- `.env` is the API's configuration. Copying the relay's example over it would wipe the database password and
  `JWT_SECRET`.
- Both compose files live in `deploy/`, so without `-p josour-relay` the two stacks would share the project name
  `deploy` and each would call the other's containers "orphans".

One secret is shared between the API and the relay (the API signs a short-lived token for each side of a session;
the relay only verifies it). It must **not** be `JWT_SECRET`.

### 3.1 On the same server as the API

Caddy already holds port 443, so the relay takes **8443**:

```bash
cd /opt/josour/deploy
cp .env.relay.example .env.relay && chmod 600 .env.relay
RELAY_SECRET=$(openssl rand -hex 32)      # or the RELAY_SECRET from env-old, if you kept it

sed -i "s|^RELAY_SECRET=.*|RELAY_SECRET=$RELAY_SECRET|; s|^RELAY_PUBLIC_PORT=.*|RELAY_PUBLIC_PORT=8443|" .env.relay
sed -i "s|^RELAY_HOST=.*|RELAY_HOST=josour.example.com|; s|^RELAY_PORT=.*|RELAY_PORT=8443|; s|^RELAY_SECRET=.*|RELAY_SECRET=$RELAY_SECRET|" .env

docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml up -d --build
docker compose up -d          # recreates api with the relay settings
```

Open **TCP 8443** in the provider's firewall if it has one (ufw does not matter for it — see [2.2](#22-prepare-the-machine-once)).

Some very restrictive networks (hotels, some offices) let only 80 and 443 out. If people need to use Josour from
those, put the relay on a machine of its own, on 443 (3.2).

### 3.2 On a server of its own

The useful place for the relay is *between* the two people: the relay sits on the path, so for an Egypt ↔ Saudi
pair a Gulf region (Jeddah, Dubai, Bahrain) costs about 40 ms while Europe costs about 150. Its own software cost is
below a millisecond ([measured](performance-relay.md)). The smallest VPS is plenty.

1. Give it a name: an `A` record `relay.example.com` → the relay server. (It needs no certificate; an IP address
   works as well as a name.)
2. Prepare it as in [2.2](#22-prepare-the-machine-once) (ufw only needs 22; Docker publishes 443 itself).
3. On the relay server:

   ```bash
   git clone https://github.com/mo-shaker/josour.git /opt/josour
   cd /opt/josour/deploy
   cp .env.relay.example .env.relay && chmod 600 .env.relay
   RELAY_SECRET=$(openssl rand -hex 32); echo "$RELAY_SECRET"      # copy it: the API needs the same value
   sed -i "s|^RELAY_SECRET=.*|RELAY_SECRET=$RELAY_SECRET|" .env.relay  # RELAY_PUBLIC_PORT stays 443
   docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml up -d --build
   ```

4. On the API server, with the same secret:

   ```bash
   cd /opt/josour/deploy
   RELAY_SECRET=<the value printed above>
   sed -i "s|^RELAY_HOST=.*|RELAY_HOST=relay.example.com|; s|^RELAY_PORT=.*|RELAY_PORT=443|; s|^RELAY_SECRET=.*|RELAY_SECRET=$RELAY_SECRET|" .env
   docker compose up -d
   ```

### 3.3 Check it

```bash
# on the relay's machine
docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml ps     # "(healthy)"
# on the API's machine
docker compose ps                                                                       # api "(healthy)"
```

From **another** network (your computer, not the server):

```bash
nc -vz josour.example.com 8443          # macOS / Linux  (relay.example.com 443 for a separate relay)
```

```powershell
Test-NetConnection josour.example.com -Port 8443          # Windows
```

> **Half a configuration stops the API at start-up, deliberately.** `RELAY_HOST` without `RELAY_SECRET` (or the
> reverse) leaves `api` restarting with `RELAY_HOST is set but RELAY_SECRET is not` in its log. Either half alone
> would make every session fall silently back to direct — the very failure the relay exists to remove. A secret
> that differs between the two files is not caught at start-up: sessions then fail at connect, with
> `Relay available` in the app's log and no `winner=Relay` after it.

---

## 4. The apps

Both apps are the same program: the same screens, both roles, the same server.

### 4.1 Getting a build

The builds come from the repository's **`release-client`** GitHub Actions workflow, which attaches them to a GitHub
Release ([releasing.md](releasing.md) is how to publish one). Download from the repository's **Releases** page
(`https://github.com/mo-shaker/josour/releases`), or straight from these links, which always give the newest:

| File | For |
|---|---|
| [`Josour-win-x64.exe`](https://github.com/mo-shaker/josour/releases/latest/download/Josour-win-x64.exe) | Windows 10/11, 64-bit. One self-contained file: nothing else to install |
| [`Josour-macos-arm64.dmg`](https://github.com/mo-shaker/josour/releases/latest/download/Josour-macos-arm64.dmg) | Macs with Apple silicon (M1 and later) |
| [`Josour-macos-x64.dmg`](https://github.com/mo-shaker/josour/releases/latest/download/Josour-macos-x64.dmg) | Intel Macs |

Each release also carries the same files under their version (`Josour-2.1.0-win-x64.exe`, …) and a
`SHA256SUMS.txt`. The .NET runtime travels inside each of them.

To build one yourself instead (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):
`powershell -ExecutionPolicy Bypass -File scripts\publish-exe.ps1` on Windows, `scripts/publish-app.sh --dmg` on a Mac
(`--arch x64` for an Intel build from Apple silicon). Do not use `dotnet publish` directly: the scripts check the
output for the native libraries a plain publish leaves behind.

**Verify what you hand to someone else.** The files are unsigned — a decision, not an omission
([ADR-0011](decisions/0011-no-code-signing-certificate.md)). A signature proves a file came from someone who bought a
certificate; **a hash proves it is this exact file**. `Get-FileHash .\Josour-2.0.0-win-x64.exe -Algorithm SHA256`
(Windows) or `shasum -a 256 Josour-2.0.0-*.dmg` (macOS), and compare it with the release's `SHA256SUMS.txt`, or with
the sender **over a call or a direct message — not the channel the file arrived on**.

### 4.2 Reinstalling over an older app

A reinstalled app **keeps its old data** — the server address, the device's identity and its saved sign-in. For a
truly clean start (and after a server rebuild you want one), remove it.

1. In the old app, turn off **Start at login** if it was on, then quit it from the tray icon (Windows) or the menu-bar
   icon (macOS) — closing the window only hides it.
2. Remove the old program (the old `Josour.exe` or its folder; `/Applications/Josour.app`).
3. Remove its data:

   ```powershell
   Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Josour"            # Windows (PowerShell)
   ```

   ```bash
   rm -rf ~/Library/Application\ Support/Josour                      # macOS (Terminal)
   ```

The new app then starts as on a new machine and asks for the server's address.

### 4.3 Windows

Before the first run, put the file somewhere permanent under a stable name — for example `C:\Josour\Josour.exe`
(renaming it is fine). *Start at login* and the optional firewall rule point at that path, and a later version can
then replace the file in place. Run it: "Windows protected your PC" appears once per machine: **More info** →
**Run anyway**.

**Optional.** A firewall rule helps only the direct path, when both machines share a network or sit behind a router
with UPnP; the relay needs no inbound connection on either side. From an elevated PowerShell, with the path you used:

```powershell
netsh advfirewall firewall add rule name="Josour Tunnel" dir=in action=allow program="C:\Josour\Josour.exe" enable=yes profile=domain,private,public protocol=TCP
```

### 4.4 macOS

Open the `.dmg` and drag **Josour** onto **Applications**. The first launch, once per machine, has to get past
Gatekeeper because the app is unsigned:

- **macOS 15 (Sequoia) and later:** open Josour; macOS says it cannot verify it — click **Done**. Then
  **System Settings → Privacy & Security**, scroll down to the line about Josour, **Open Anyway**, and confirm with your
  password.
- **macOS 14 and earlier:** **right-click** the app → **Open** → **Open**.
- Either version, from Terminal instead: `xattr -dr com.apple.quarantine /Applications/Josour.app`

After that it opens normally.

The **first time this Mac is the guest in a session**, macOS may ask for an administrator's password: that is the app
asking to change the system's proxy setting, which is how every browser on the Mac goes through the host.

### 4.5 First run

The interface is **in Arabic by default**, right to left. There is a language setting inside the app (it applies on
the next start), or start it once in English:

```
C:\Josour\Josour.exe --lang en                            # Windows
/Applications/Josour.app/Contents/MacOS/Josour --lang en   # macOS, from Terminal
```

The first run asks for:

1. **The server's address** — `https://josour.example.com`. It is checked before you can continue (the app asks
   `/healthz` and looks for the product marker), so a wrong address fails here rather than later as "wrong password".
2. **Sign in**, or **Create account** (shown unless registration is *Closed*). In approval mode a new account then
   waits until an administrator approves it under *Registrations* in the dashboard (or
   `docker compose exec api python manage.py approve-user them@example.com`); until then, signing in says the account
   is waiting for approval.
3. **A readiness summary** — including a warning if a VPN holds this machine's route to the internet.

> **If you use a VPN on the host machine, turn it off.** The sites will see the VPN's address, not the machine's,
> which defeats the point. The app warns you; it does not stop you.

Closing the main window leaves the app running in the tray (Windows) or the menu bar (macOS).

---

## 5. Accounts and contacts

**Hosts are visible only to contacts** ([ADR-0014](decisions/0014-contacts-and-server-held-trust.md)). A brand-new account sees nobody, and
nobody can ask to use its connection, until it has contacts.

**The normal way — people invite each other in the app.** One person invites the other **by email**; the invitation
appears in the other person's app (no email is actually sent), and once they accept, each sees the other. An
invitation to an address with no account yet waits for it: it is delivered if that person registers (and is approved)
before it expires — after `invite_ttl_days`, 30 by default (*Settings*). A decline is silent.

**The shortcut for a small, trusted group — link everyone:**

```bash
cd /opt/josour/deploy
docker compose exec api python manage.py seed-contacts --all-active
```

Every active account becomes a contact of every other. It only adds links, so it is safe to run again after you add
accounts; each person can still remove contacts in the app, and you can in the dashboard (*Users* → the person).

**The rest of user management** is in the dashboard ([2.7](#27-the-dashboard-and-locking-it-down)), and the same
operations exist on the command line for a server with no browser at hand:

```bash
docker compose exec api python manage.py create-user  --email them@example.com --display-name "Name"   # asks for the password
docker compose exec api python manage.py create-admin --email them@example.com --display-name "Name"
docker compose exec api python manage.py list-pending
docker compose exec api python manage.py approve-user them@example.com
docker compose exec api python manage.py reject-user  them@example.com
docker compose exec api python manage.py delete-user  them@example.com
docker compose exec api python manage.py list-sessions
docker compose exec api python manage.py end-session <session-id>
docker compose exec api python manage.py --help
```

**Deleting an account** anonymises it rather than erasing rows: the history of the sessions it shared with other people
stays theirs. Disabling it (*Users*) signs it out and ends any live session at once, and can be undone.

### 5.1 The site list — optional, and off by default

Since [ADR-0010](decisions/0010-route-all-through-host.md) **everything** the guest browses goes through the host, and
the allow-list is not enforced. You do not need to add a single domain.

To restrict a deployment to specific destinations: add them under *Allow-list* (or `manage.py add-domain example.com`),
then tick *Restrict sites to the allow-list* under *Settings* (`PATCH /api/v1/admin/settings` with
`{"enforce_allowlist": true}` does the same over the API).

> The site list is **one** of the egress-policy steps. Blocking the host's internal addresses, refusing address
> literals, strict name normalisation and the limit to the *Allowed ports* (80 and 443) stay as they are either way.

---

## 6. The first end-to-end test

Two machines, **on two different networks** — for example the host on the home Wi-Fi and the guest on a phone's
hotspot. Both apps installed, both signed in, the two accounts contacts of each other.

1. **Note both addresses first.** On each machine open `https://api.ipify.org` in a browser and write the number down.
   They must differ; if they do not, the machines are not on two networks and the test will prove nothing.
2. **On the host** — the machine sharing its connection: make it available to receive requests (the availability
   switch in the main window).
3. **On the guest** — the machine borrowing it: the host appears in the list within seconds. Select it, choose a
   duration and send the request.
4. **On the host: the consent.** A window (and a notification) shows who is asking, from which device, for how long,
   the browsing scope, and that the sites will see this machine's address. You have 60 seconds. **Accept.**
   ("Always let this contact use my connection without asking" is held by the server for this contact, on every
   device you sign in from ([ADR-0014](decisions/0014-contacts-and-server-held-trust.md)); leave it unticked for the test.)
5. **On the guest: the proof.** The default browser opens the check page (`http://check.josour/`) by itself and shows
   the address sites will see — the **host's**. In **any** browser, reload `https://api.ipify.org`: the host's address
   must appear there too. That is the whole product. (On a Mac the first session may ask for the administrator
   password first — see [4.4](#44-macos). If the session panel warns that the check page did not come through, that
   browser is set to ignore the system proxy; try another.)
6. **End it** from either side. On the guest, `https://api.ipify.org` shows its own address again: the app has put
   the system proxy back.

While a session runs, the host's local network, its router's page and its `localhost` services stay out of the
guest's reach — checked after every name is resolved and before any connection is opened, whatever the site list
says. The session also ends by itself when its duration runs out.

To see **how** it connected, look at the app's log (next part): `winner=Relay` is the relay; `Lan` or `Upnp` is a
direct path. In the dashboard, *Sessions* lists the session with its end reason.

---

## 7. Running it

### 7.1 Updating

Servers follow **release tags** ([releasing.md](releasing.md)), not every commit on `main`. The simplest way, whether
or not the server was installed with it:

```bash
sudo bash /opt/josour/deploy/install.sh
```

It keeps `.env` as it is, takes a backup, checks out the newest release, rebuilds both stacks and waits for them to
be healthy. By hand, the same:

```bash
cd /opt/josour/deploy
docker compose exec backup /usr/local/bin/backup.sh           # a backup before anything changes
cd /opt/josour && git fetch --tags && git checkout "$(git tag -l 'v*' --sort=-v:refname | head -1)"
cd deploy
docker compose up -d --build                                  # rebuilds api; migrations run as it starts
docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml up -d --build   # if the relay runs here
docker compose ps && curl -s https://josour.example.com/healthz
docker image prune -f
```

Restarting `api` drops every app's WebSocket for a moment: the apps reconnect on their own, and a session running at
that moment ends. Update when nobody is in a session. Details and the staging workflow: [runbook.md](runbook.md).

### 7.2 Backups

The `backup` service keeps `deploy/backups/josour-<UTC time>.sql.gz` (daily, 14 days by default). Copy them **off the
server**, e.g. from your computer:

```bash
rsync -av root@josour.example.com:/opt/josour/deploy/backups/ ./josour-backups/
```

Also keep a copy of `deploy/.env` (and `deploy/.env.relay`) somewhere safe: without `POSTGRES_PASSWORD` a backup
still restores, but without `.env` you are rebuilding the configuration by hand.

### 7.3 Restoring

```bash
cd /opt/josour/deploy
./backup/restore.sh backups/josour-YYYYMMDDTHHMMSSZ.sql.gz
```

It stops `api`, drops and recreates the database, imports the file and starts `api` (which applies newer migrations
if the dump is older). Everything written since that backup is lost. To restore onto a **new** server: install it as
in [part 2](#2-the-server) up to 2.4, put the file in `deploy/backups/`, and run the same command
([2.6](#26-restore-the-old-database-optional)).

---

## 8. If it does not work

### On the server

| What you see | What it means | What to do |
|---|---|---|
| `required variable … is missing a value` from `docker compose` | One of the four required values is empty | Set it in `deploy/.env` ([2.3](#23-fetch-the-code-and-configure-it)) |
| `curl https://…/healthz` fails; `docker compose logs caddy` mentions the ACME challenge | Let's Encrypt cannot reach the server | DNS not pointing here yet, or port 80 closed in the provider's firewall |
| `api` keeps restarting; its log says `password authentication failed` | `POSTGRES_PASSWORD` changed after the first start | Put the original value back. On a server with nothing to keep, `docker compose down -v` (**deletes the database**) and `up -d` again |
| `api` keeps restarting; `RELAY_HOST is set but RELAY_SECRET is not` (or the reverse) | Half a relay configuration | Set both, or neither ([3](#3-the-relay--what-makes-a-session-work-with-no-router-configuration)) |
| `api` keeps restarting; a validation error about `jwt_secret` or `RELAY_SECRET must be at least 32 characters` | A secret is too short | `openssl rand -hex 32` |
| A change in `.env` had no effect | `docker compose restart` does not re-read `.env` | `docker compose up -d` |
| `/admin` answers 404 | Your address is outside `ADMIN_WEB_ALLOWED_CIDRS` (or `ADMIN_WEB_ENABLED=false`) | `docker compose logs --tail 20 api` shows the address the API sees for your requests; fix the list and `docker compose up -d` |
| The dashboard refuses the sign-in form ("forbidden") | It was opened through another address than `https://DOMAIN` (an IP, another name) | Use `https://DOMAIN/admin` |
| Lost the authenticator app | — | `docker compose exec db psql -U josour -d josour -c "update users set totp_secret = null, totp_enabled_at = null, totp_last_step = null where email = 'you@example.com';"`, sign in, enrol again |
| Forgot the administrator's password | — | `create-admin` a second administrator, sign in as it, *Users* → the first → set a password |
| The relay stack complains about orphans, or the API's containers disappear after a relay command | The relay was started without `-p josour-relay` | Always pass `-p josour-relay --env-file .env.relay -f docker-compose.relay.yml` |

`psql -U josour -d josour` assumes the default `POSTGRES_USER`/`POSTGRES_DB`; if you reused an old `.env`, use the
names in it.

### In the app

The app's log names the failure:

```powershell
Get-Content "$env:LOCALAPPDATA\Josour\logs\app-*.log" | Select-String "Tunnel connected|connect_failed|Relay available" | Select-Object -Last 5
```

```bash
grep -E "Tunnel connected|connect_failed|Relay available" ~/Library/Application\ Support/Josour/logs/app-*.log | tail -5
```

| What you see | What it means | What to do |
|---|---|---|
| The server address is refused at first run | `https://DOMAIN/healthz` is not answering as Josour | Open it in a browser; fix the server first |
| "Waiting for approval" at sign-in | Approval mode | Approve under *Registrations*, or `manage.py approve-user` |
| "Update required" | The app is older than *Oldest app version accepted* | Install the current release, or lower the setting |
| The host is not in the guest's list | Not contacts, or the host is not available / offline | Invite each other ([part 5](#5-accounts-and-contacts)); turn on availability on the host |
| `winner=Relay` | The tunnel stood up over the relay | Sound |
| `winner="Lan"` or `"Upnp"` | Direct won — faster, but the two networks are not different enough to prove the address changed | Put the machines on two networks |
| `connect_failed` with `Relay available` above it | The relay is announced and did not work | Check its port from outside ([3.3](#33-check-it)), and that `RELAY_SECRET` **matches** in `.env` and `.env.relay` |
| `connect_failed` with no `Relay available` | The server is not announcing a relay | `RELAY_HOST`/`RELAY_SECRET` missing from `deploy/.env`, or `docker compose up -d` not run after setting them |
| The guest has no internet after the app crashed or was killed mid-session | The system proxy was left pointing at the app | Start Josour again, or switch the proxy off by hand: Windows *Settings → Network & internet → Proxy* (manual proxy off); macOS *System Settings → Network → the connection → Details → Proxies* (all off) |
| macOS: "Josour cannot be opened" | Gatekeeper, first launch | [4.4](#44-macos) |
| Windows: nothing opens and nothing is logged | A build made with plain `dotnet publish` | Use the release, or `scripts\publish-exe.ps1` |

**To see whether a direct path was ever possible** on a given network, and for a headless end-to-end check that
separates a tunnel failure from an interface failure, see [spike-runbook.md](spike-runbook.md).

---

## Verification summary

| # | Check | Expected |
|---|---|---|
| 1 | `curl https://josour.example.com/healthz` | `{"status":"ok","product":"josour","version":"2.0.0"}` |
| 2 | `https://josour.example.com/admin` | The sign-in page; your administrator gets in (with its second factor once enrolled) |
| 3 | The relay's port from another network | Open |
| 4 | Signing in from both machines | Succeeds |
| 5 | The host appears in the guest's list | Within seconds, once they are contacts |
| 6 | The request window on the host | Name, device, duration, browsing scope, and the warning that sites will see your address |
| 7 | `api.ipify.org` in any browser on the guest, during the session | **The host's address** |
| 8 | The same page after the session ends | **The guest's own address** again |
| 9 | Ending the session from either side | Both apps show it ended within seconds |
| 10 | The duration running out | The session ends on its own |

The full list is in [acceptance-checklist.md](acceptance-checklist.md).

---

## References

| File | Contents |
|---|---|
| [runbook.md](runbook.md) | Operating the server: upgrading, backup and restore, monitoring, the staging workflow |
| [deploy/.env.example](../deploy/.env.example) · [deploy/.env.relay.example](../deploy/.env.relay.example) | Every server setting, with its default |
| [relay/README.md](../relay/README.md) | The relay: what it does and does not do, its wire contract and limits |
| [macos-port.md](macos-port.md) | What the Mac build does differently |
| [spike-runbook.md](spike-runbook.md) | The headless tool: every command and every JSON event |
| [acceptance-checklist.md](acceptance-checklist.md) | The success criteria and the security checks |
| [api.md](api.md) · [ws-protocol.md](ws-protocol.md) · [protocol.md](protocol.md) | The contracts |
