# Self-hosting: deploying the server and installing the application

A practical guide from nothing. It assumes you have prepared nothing yet.

**Nobody runs a public relay** — whoever wants Josour runs their own server, and this guide is the way. What you get at
the end: a server running on the internet, two machines with the application on them, and a session proving that the
sites see the host's address rather than yours.

> **Read [the host's responsibility in the README](../README.md) before you run this for anyone.** You are about to
> make your home's address the exit for someone else's browsing.

> **The limits of what this guide deploys today:**
> - **Both roles work on Windows and macOS.** What has not been tried yet is a real session between the two systems,
>   which needs two machines; every piece separately has been exercised. The detail is in
>   [macos-port.md](macos-port.md).
> - **No code-signing certificate** ([ADR-0011](decisions/0011-no-code-signing-certificate.md)), so SmartScreen will
>   warn on Windows and Gatekeeper on macOS. The guide explains how to get past the warning knowingly. And on macOS
>   specifically: **right-click the application → Open → Open**, once. Double-clicking it first says "cannot be opened"
>   with no way out.
> - **Building the macOS version:** `scripts/publish-app.sh --dmg` produces a `Josour.app` and a `.dmg` that need no
>   .NET installed on the user's machine.
> - **A full session between two machines on two different networks actually took place on 2026-09-09**: the tunnel
>   stood up over the relay with TLS 1.3, and the sites saw the host's address. What you read here is an exercised path,
>   not a proposed one.
> - **The headless `session` tool** remains the fastest way to isolate a tunnel failure from an interface failure, but
>   it is no longer a prerequisite for starting.
> - **No support.** If you get stuck, part four covers the known failures, and the source is open.

---

## Part one: the server

### 1.1 What you need

| Item | Specification | Approximate cost |
|---|---|---|
| A VPS | One core, 2 GB of memory, Ubuntu 24.04 LTS | 4 to 6 dollars a month |
| A domain name | A subdomain such as `rb.example.com` | From a domain you own |

The providers named in the product document: Hetzner, DigitalOcean, Contabo or Hostinger (**a VPS plan, not shared
hosting**; the application needs a long-running Python process and long-lived WebSocket connections).

This specification's capacity is measured rather than estimated: **500 concurrent control channels**
(`docs/load-test-week5.md`).

### 1.2 The DNS record (before anything else)

Create an `A` record pointing `rb.example.com` at the server's address, and wait for it to propagate:

```bash
dig +short rb.example.com
```

The server's address must appear. **Do not continue before that**: Caddy requests a TLS certificate from Let's Encrypt
on its first run, and will fail if it does not find the record.

### 1.3 Preparing the server (once)

Connect to the server over SSH and run:

```bash
apt update && apt -y upgrade
apt -y install ufw fail2ban unattended-upgrades ca-certificates curl git
ufw default deny incoming && ufw default allow outgoing
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp
ufw --force enable
dpkg-reconfigure -plow unattended-upgrades
curl -fsSL https://get.docker.com | sh
```

`ufw` opens three ports only. The database is never exposed to the internet.

### 1.4 Fetching the code

```bash
git clone https://github.com/<your-account>/routebridge.git /opt/routebridge
cd /opt/routebridge/deploy
```

If the repository is private, git will ask you for credentials. Use a personal access token with the `repo` scope from
[github.com/settings/tokens](https://github.com/settings/tokens).

### 1.5 The settings

```bash
cp .env.example .env
nano .env
```

You **must** set these values:

| Variable | Value |
|---|---|
| `DOMAIN` | `rb.example.com` |
| `ACME_EMAIL` | Your email (for certificate-expiry notices) |
| `POSTGRES_PASSWORD` | A long random string: `openssl rand -base64 32` |
| `JWT_SECRET` | **48 random bytes**: `openssl rand -base64 48` |

Leave the rest. **Do not commit `.env` to git** (it is excluded in `.gitignore`).

> `JWT_SECRET` must be at least 32 bytes or the server refuses to start. And changing it later invalidates every access
> token at once and forces everyone to sign in again.

### 1.6 Starting it

```bash
docker compose up -d --build
docker compose logs -f api
```

Wait for `Application startup complete`, then press `Ctrl+C` to leave the log (the service keeps running).

Check:

```bash
curl -s https://rb.example.com/healthz
```

Expected: `{"status":"ok","product":"josour","version":"0.1.0"}`

> If it fails: `docker compose logs caddy` usually explains the certificate failure. The two commonest causes: the DNS
> record has not propagated, or port 80 is closed (Let's Encrypt needs it to validate).

### 1.7 Creating the accounts

```bash
docker compose exec api python manage.py create-admin \
  --email admin@example.com --password 'a-strong-password' --display-name "Admin"

docker compose exec api python manage.py create-user \
  --email host@example.com --password 'Host-pass-1234' --display-name "The host machine"

docker compose exec api python manage.py create-user \
  --email guest@example.com --password 'Guest-pass-1234' --display-name "The user machine"
```

> There is no self-registration: every user is created by an administrator, and that is deliberate in the first
> release.

### 1.8 The site list — optional now

**Skip this step.** Since [ADR-0010](decisions/0010-route-all-through-host.md) **everything** the work browser asks for
goes through the host, and the `enforce_allowlist` setting is off by default. There is no need to add a single domain
before trying it.

The list remains a control you can turn on later if you want to restrict your deployment to specific destinations:

```bash
docker compose exec api python manage.py add-domain api.ipify.org
docker compose exec api python manage.py list-domains
```

Then enable the restriction through `PATCH /admin/settings` with `{"enforce_allowlist": true}`.

> What disabling the list does not change: blocking internal addresses at the host (its local network, its router's
> page, `localhost`, its public address) and the limit to ports 80 and 443. "All sites" lifts the name condition alone.

### 1.9 The relay service — what makes a session stand up with no router configuration

[ADR-0009](decisions/0009-relay-default.md): the relay is the default transport, and direct is an upgrade attempted in
parallel that wins when it succeeds. Without it a session works **only** when one of the two sides is reachable from
the internet — which does not happen between two mobile networks, the case that failed twice before it was built.

**The same secret in two places.** Generate it once:

```bash
openssl rand -base64 48
```

Start the service:

```bash
cd /opt/routebridge/deploy && cp .env.relay.example .env.relay && nano .env.relay
```

Set `RELAY_SECRET` to the secret, and `RELAY_PUBLIC_PORT=8443` **if you are running it on the same server as the API**
(Caddy occupies 443). On a separate server leave it at 443.

```bash
docker compose --env-file .env.relay -f docker-compose.relay.yml up -d --build
ufw allow 8443/tcp
```

Then tell the backend server about it — add to `deploy/.env`:

```bash
RELAY_HOST=rb.example.com
RELAY_PORT=8443
RELAY_SECRET=<the same secret>
```

And redeploy it: `docker compose up -d --build`.

> **Half a configuration stops the server at startup deliberately:** an address with no secret issues no token, a
> secret with no address is not sent, and either makes every session fall silently back to direct — the very failure
> ADR-0009 exists to remove.

> **Where you put it matters more than its capacity:** the relay sits on the path between the two sides. Egypt ↔ Saudi
> Arabia via Jeddah is about 40 milliseconds, and via Europe about 150. Its own software cost is **below a
> millisecond** ([measured](performance-relay.md)), so geography alone is what the user feels.

**Check it from outside:**

```bash
curl -s https://rb.example.com/healthz          # {"status":"ok","product":"josour",...}
timeout 5 bash -c "</dev/tcp/rb.example.com/8443" && echo "the port is open"
```

### 1.10 A first backup

```bash
docker compose exec backup /usr/local/bin/backup.sh
ls -la backups/
```

Backups are automatic daily after that. **Copy the `backups/` directory off the server regularly**; a copy on the same
disk is not a disaster plan. The restore script `./backup/restore.sh` has actually been exercised and is documented in
`docs/runbook.md`.

---

## Part two: the two machines

You need **two machines**, preferably on **two different networks** (one of them on a mobile network, say). One machine
works but does not prove the thing that matters: that the traffic leaves from the other machine's address.

### 2.1 Getting the program

Now that the repository is on GitHub, the easiest route is **a CI build**:

1. Open your repository → the **Actions** tab → the last successful run of `ci-client`.
2. Download the attached `josour-app-unsigned` artefact.
3. Unpack it into a permanent directory, such as `C:\Josour`.

> If no run appears, push any change to `main` or run the workflow by hand from the same tab.

**The alternative: building locally** (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
git clone https://github.com/<your-account>/routebridge.git
cd routebridge
powershell -ExecutionPolicy Bypass -File scripts\publish-exe.ps1
```

It produces **one file** at `client\publish\exe\Josour.exe`, and prints its hash. And do not build with `dotnet
publish` directly: the native libraries are not bundled by default, so you get a file that works on the build machine
**and dies silently** on any other. The script checks for that and refuses an incomplete output.

### 2.2 Verifying the file, then getting past SmartScreen

The file is **unsigned, and that is a decision rather than an omission**
([ADR-0011](decisions/0011-no-code-signing-certificate.md)): the users are three to five people who know who gave them
the file, and an OV certificate does not remove the SmartScreen warning at that number anyway — it builds reputation by
download count.

**The alternative is not weaker than signing.** A signature proves the file came from a party that bought a
certificate; **the hash proves it is this exact file**. So, before running it on any machine:

```powershell
Get-FileHash C:\Josour\Josour.exe -Algorithm SHA256
```

Match the output against the hash `publish-exe.ps1` printed at build time — **over a call or a direct message, not over
the same channel the file arrived on**. A mismatch means a different file: delete it.

After it matches, "Windows protected your PC" appears once per machine: **More info** then **Run anyway**.

> **When does this change?** When the answer to "does every user know who gave them the file?" becomes "no". Then the
> certificate comes back to the table, and with it the Inno Setup installer already in `client/installer/`.

### 2.3 The firewall rule (optional now)

**It is no longer a requirement.** With the relay neither side opens a listener: both connect **outbound**, and an
outbound connection does not go through an inbound rule. The session stands up without it.

Add it only to speed up the direct path when the two machines are on one network or behind a router with UPnP — direct
then beats the relay and is faster. From PowerShell **as an administrator**:

```powershell
netsh advfirewall firewall add rule name="Josour Tunnel" dir=in action=allow program="C:\Josour\Josour.exe" enable=yes profile=domain,private,public protocol=TCP
```

> Without it Windows blocks the user's inbound connection **silently**, and it looks like a network failure. The name
> must be exactly `Josour Tunnel`: the application checks for a rule by that name and warns you if it is missing.

To remove it after the trial:

```powershell
netsh advfirewall firewall delete rule name="Josour Tunnel"
```

### 2.4 The first run

Run `Josour.exe`. The interface is **in Arabic** with right-to-left direction (`--lang en` for English).

It will ask you for:
1. **The server's address**: `https://rb.example.com` — it actually checks it before letting you continue.
2. **Signing in**: `host@example.com` on the first machine and `guest@example.com` on the second.
3. **A readiness summary**: the firewall rule's state, and a VPN warning if a VPN adapter holds the egress route.

> **If you are using a VPN on the host machine, turn it off.** The sites will see the VPN's address rather than the
> machine's, which defeats the point of the trial. The application warns you but does not stop you.

### 2.5 The session

**On the host machine:** enable "available to receive requests".

**On the user's machine:** the host appears in the list. Select it, choose a duration (15 minutes is enough), and
request the connection.

**On the host machine:** a notification and a window appear showing the requester's name, their device, the duration,
**the browsing scope** — which, with the list off, says they will be able to browse any site over your connection — and
the warning that the sites will see your IP address. Accept.

**On the user's machine:** a separate work browser opens on the check page. Go to `https://api.ipify.org`.

**That is the whole test:** **the host machine's IP address** must appear, not yours. Open your ordinary browser on the
same address at the same moment — your own address must appear. The two numbers differing is the product.

---

## Part three: the surest route — the `session` tool

If the interface gives you trouble, this tool proves the tunnel itself works, and it has **actually been exercised**
end to end.

It needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on both machines, and a cloned repository.

**On the host machine:**

```powershell
cd routebridge\client
dotnet run --project tools\Josour.Spike -c Release -- session `
  --api https://rb.example.com --email host@example.com --password "Host-pass-1234" `
  --role host --available --state-dir C:\rb-host
```

Wait until it prints that it is waiting for a request, and note the `device_id` it shows.

**On the user's machine:**

```powershell
cd routebridge\client
dotnet run --project tools\Josour.Spike -c Release -- session `
  --api https://rb.example.com --email guest@example.com --password "Guest-pass-1234" `
  --role guest --list-hosts --state-dir C:\rb-guest
```

Copy the host's `device_id` from the list, then:

```powershell
dotnet run --project tools\Josour.Spike -c Release -- session `
  --api https://rb.example.com --email guest@example.com --password "Guest-pass-1234" `
  --role guest --host-device <device_id> --minutes 15 `
  --curl-test https://api.ipify.org --state-dir C:\rb-guest
```

**The result you want** is a JSON line containing:

```json
{"event":"curl.result","status":200,"body_prefix":"<the host's IP address>"}
```

If `body_prefix` is **the host machine's** address, the product works. The exit codes: `0` success, `2` the connection
failed, `3` the tunnel died, `4` an authentication error.

> `--host-device` accepts the device's id or its name, **not the email address**.

---

## Part four: if the connection does not succeed

The decision gate this section used to serve **was closed** on 2026-09-07
([ADR-0009](decisions/0009-relay-default.md)): the measurement on a real pair proved the direct path impossible between
two sides behind CGNAT, so the relay became the default transport. If the relay is configured the session should stand
up; and if it does not, read the reason instead of guessing.

### The log names the failure

```powershell
Get-Content "$env:LOCALAPPDATA\Josour\logs\app-*.log" | Select-String "Tunnel connected|connect_failed|No probe page" | Select-Object -Last 5
```

| What you read | What it means | The step |
|---|---|---|
| `winner="Relay"` | The tunnel stood up over the relay | Sound |
| `winner="Lan"` or `"Upnp"` | Direct won — faster, but the two networks are not different enough to prove the address changed | Put the machines on two networks |
| `connect_failed` with `Relay available` in the preceding lines | The relay is announced and did not succeed | Check that its port is open from outside, and that `RELAY_SECRET` **matches** in both files |
| `connect_failed` with no `Relay available` | The server is not sending the `relay` object at all | `RELAY_HOST` or `RELAY_SECRET` is missing from `deploy/.env` |
| `No probe page ... (accepted=N rejected_by_owner=N)` | The work browser reaches the proxy and the proxy refuses it | A defect in the application — send the line |
| `No probe page ... (nothing ever connected)` | The browser is not using the proxy | [`scripts/diagnose-work-browser.ps1`](../scripts/diagnose-work-browser.ps1) settles it |

### Diagnosing the browser

```powershell
powershell -ExecutionPolicy Bypass -File scripts\diagnose-work-browser.ps1
```

It runs the browser with Josour's own arguments against an empty listener, and prints a verdict: it never arrived
(something overrides `--proxy-server` on that machine), or it arrived late (the timeout is short on that machine), or
it arrived at once (the browser is fine and the difference is in Josour's proxy).

### The direct path alone

To gather NAT data with no relay:

```powershell
dotnet run --project tools\Josour.Spike -c Release -- gather --port 40000 --public-ip <your public address>
```

`upnp_found: false` with `ipv6_global: false` and a lone `public` candidate means direct is impossible from this
network — which is exactly what the relay carries.

---

## Verification summary

| # | Check | Expected |
|---|---|---|
| 1 | `curl https://rb.example.com/healthz` | `{"status":"ok","product":"josour",...}` |
| 2 | Signing in from both machines | Succeeds |
| 3 | The host appearing in the user's list | Appears within seconds |
| 4 | The request window on the host | Shows the name, the device and the duration, and under the heading **"browsing scope"** the sentence **"they will be able to browse any site over your connection"**, and the warning that the sites will see your address. **And no sentence about "the company's list" appears** |
| 5 | `api.ipify.org` in the work browser | **The host's address** |
| 6 | The same address in the ordinary browser | **Your own address** |
| 7 | Teams and Outlook during the session | Work on your address, unaffected |
| 8 | Disconnecting from either side | Closes the work browser within seconds |
| 9 | The duration running out | The session ends automatically |

The complete list (18 items) is in `docs/acceptance-checklist.md`.

---

## References

| File | Contents |
|---|---|
| `docs/runbook.md` | Running the server: upgrading, backup and restore, monitoring, automatic deployment |
| `docs/spike-runbook.md` | The prototype tool in detail: every command and every JSON event |
| `docs/test-matrix.md` | The device, network and edge-case matrix |
| `docs/acceptance-checklist.md` | The 18 success criteria and the security checks |
| `docs/load-test-week5.md` | The server's measured capacity |
| `client/installer/README.md` | Building the signed installer once the certificate arrives |
