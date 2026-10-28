# Josour

A desktop application for Windows and macOS that lets one person (the guest) use another person's internet
connection (the host's) temporarily and with their explicit consent, so that sites which only work from inside the
host's country become reachable — an expatriate who needs their bank or their government's portal. While a session
lasts, the guest's computer points its proxy settings at Josour, so **any browser** on it leaves from the host's address
([ADR-0010](docs/decisions/0010-route-all-through-host.md), [ADR-0015](docs/decisions/0015-system-proxy-instead-of-work-browser.md));
when the session ends the settings go back exactly as they were.

> ## Read this before you host
>
> **When you share your connection, sites see your address.** Everything your guest browses looks to the world —
> and to your ISP, and to anyone investigating later — as if it came **from your home, under your name**. That is
> not a side effect. It is the point: without it the tool does nothing.
>
> So host only for people whose unknown actions you are willing to answer for. The tool gives you a bounded
> duration, an immediate disconnect and a session log — but it **cannot make what someone else does their
> responsibility alone**, and no software can.
>
> This is also a category of software (routing traffic through a residential address) that is widely abused: ad
> fraud, credential stuffing, evading bans. Know that while you decide.
>
> What does **not** happen: nothing on your own computer changes, and your local network and `localhost` services stay
> out of reach. On the guest's side, every program that follows the system proxy settings (any browser) goes through
> you for as long as the session lasts, and only then.

User management happens in a **web dashboard on your own server**, at `https://<your domain>/admin`
([ADR-0016](docs/decisions/0016-web-admin-dashboard.md)): approving the people who register from the app
([ADR-0017](docs/decisions/0017-self-registration-with-approval.md)), disabling, deleting or unlocking accounts,
revoking devices, removing contacts, ending sessions, and the server's settings. An administrative panel on the
internet is a high-value target, so it is plain server-rendered pages with no JavaScript, its own short-lived
sessions with CSRF protection, an optional authenticator-app second factor and an optional network allowlist — and
`ADMIN_WEB_ENABLED=false` removes it.

People share a connection only with their **contacts**: you add someone by email, and once they accept, either of
you can ask for the other's connection or offer your own
([ADR-0014](docs/decisions/0014-contacts-and-server-held-trust.md)). You can let a contact use your connection without
asking you each time; you are still told when they connect, and can cut them off at any moment.

Browsing data is encrypted end to end between the two machines: **the server cannot read it**. When a direct
connection is impossible — which is the normal case between two mobile networks — the opaque bytes travel through
a relay that does not understand them ([ADR-0009](docs/decisions/0009-relay-default.md)), so a session works with
no router or firewall configuration at all.

## License and reuse

Josour is published as a service to the community under the [Apache License 2.0](LICENSE).
You may use, copy, modify, fork and redistribute it, including commercially and in proprietary products,
subject to that licence. You do not have to publish your changes or pay the original author.
When redistributing, include the licence, preserve applicable notices and mark modified files.
Third-party components keep their own licences.

The software is provided **as is, without warranty or a promise of support**. The licence limits the liability
of its contributors to the extent permitted by applicable law; it does not override liability that the law
does not allow to be excluded. No additional permission is needed for uses allowed by the licence.
See [licensing and distribution](docs/licensing.md) for details and an Arabic summary.

## Layout

| Directory | Contents |
|---|---|
| `backend/` | Control server: Python 3.12 + FastAPI + PostgreSQL + WebSockets |
| `client/` | .NET 8 solution: tunnel, proxy and egress libraries + the Avalonia app ([ADR-0013](docs/decisions/0013-avalonia-and-macos.md)) |
| `relay/` | Relay service: pairs the two ends of a session and pumps opaque bytes ([ADR-0009](docs/decisions/0009-relay-default.md)) |
| `deploy/` | The one-command installer (`install.sh`), production Docker Compose (Caddy + API + PostgreSQL + backups) and a **standalone relay** |
| `docs/` | Implementation plan, the frozen contracts (REST / WebSocket / channel protocol), ADRs, runbook |

## Download the app

| Windows 10/11 | Mac with Apple silicon (M1 and later) | Mac with an Intel processor |
|---|---|---|
| **[Josour-win-x64.exe](https://github.com/mo-shaker/josour/releases/latest/download/Josour-win-x64.exe)** | **[Josour-macos-arm64.dmg](https://github.com/mo-shaker/josour/releases/latest/download/Josour-macos-arm64.dmg)** | **[Josour-macos-x64.dmg](https://github.com/mo-shaker/josour/releases/latest/download/Josour-macos-x64.dmg)** |

These links always give the newest release; every version, its notes and a `SHA256SUMS.txt` to check the files
against are on the [Releases page](https://github.com/mo-shaker/josour/releases), and what changed is in
[CHANGELOG.md](CHANGELOG.md). Nothing else needs installing: .NET travels inside each file. They are the same
application — one codebase, one interface, both roles (host and guest) on both systems
([ADR-0013](docs/decisions/0013-avalonia-and-macos.md)).

Both are unsigned by decision ([ADR-0011](docs/decisions/0011-no-code-signing-certificate.md)), so the first launch,
once per machine, needs one extra click: on Windows, SmartScreen → **More info → Run anyway**; on a Mac,
**right-click → Open → Open** (macOS 15: System Settings → Privacy & Security → **Open Anyway**).

The app needs a Josour server to sign in to. Nobody runs a public one: you install your own, below.

## Install a server

On a fresh Ubuntu 24.04 server whose name already points at it, one command does everything — firewall, Docker,
certificate, secrets, the relay, the administrator and the daily backups — and the same command updates it later:

```bash
curl -fsSL https://raw.githubusercontent.com/mo-shaker/josour/main/deploy/install.sh -o josour-install.sh
sudo bash josour-install.sh
```

- **[docs/install-server.md](docs/install-server.md)** ([بالعربية](docs/install-server.ar.md)) — step by step, for
  someone who has never run a server: renting it, naming it, the command, the dashboard, the apps, a first session.
- **[docs/self-hosting.md](docs/self-hosting.md)** — the same by hand, with the reasons for every step; rebuilding an
  existing server, restoring a backup, a relay on a machine of its own, and troubleshooting.
- **[docs/releasing.md](docs/releasing.md)** — publishing a new version of the apps and the server.

To build the apps yourself instead: `scripts\publish-exe.ps1` on Windows (one `Josour.exe`), `scripts/publish-app.sh
--dmg` on a Mac (`Josour.app` and a `.dmg`).

**Status:** a full session between two machines on different networks has been working since 2026-09-09 (milestone
M2). Latest report: [docs/status-week7.md](docs/status-week7.md).

The full plan: [docs/Josour-MVP-Implementation-Plan.md](docs/Josour-MVP-Implementation-Plan.md)

## Where this project stands, honestly

One person maintains it, and it is published as a contribution to the community. **No support, no warranty, and no
contributions accepted** — issues and pull requests are closed, and that is honesty rather than rudeness: accepting
a change to a tool that carries other people's traffic means reading every line for whether it quietly widens the
egress policy, and I do not have the time to do that properly. Fork it and change it as you like
([Apache-2.0](LICENSE)).

**Nobody runs a public relay.** Anyone who wants to use this hosts their own server and relay; the installer above
sets up both ([docs/install-server.md](docs/install-server.md)).

**macOS is supported in both roles** — host and guest — and so is Windows. What has not been tried yet: a real
session between a Mac and a Windows machine, which needs two machines; every piece of it is exercised separately.
The detail, and what remains, is in [docs/macos-port.md](docs/macos-port.md).

**Getting in touch:** issues and pull requests are closed, so anything you want to say comes by email —
**me@mohamedshaker.com**. That is not a support channel and there is no response time; it is simply the one address
that reaches me.

To report a security vulnerability: [SECURITY.md](SECURITY.md) — **do not open a public issue**, write to the same
address.

## The frozen contracts

- [docs/api.md](docs/api.md): the REST interfaces
- [docs/ws-protocol.md](docs/ws-protocol.md): WebSocket messages and the session lifecycle
- [docs/protocol.md](docs/protocol.md): the channel between the two machines (TLS, authentication, frames)
- [docs/decisions/](docs/decisions/): the ADRs

A change to a contract goes through review by both affected sides, and the document is updated before the code.

## Running it locally

### The server

```bash
cd backend
python3.12 -m venv .venv && source .venv/bin/activate
pip install -e ".[dev]"
cp .env.example .env            # set JWT_SECRET (32 bytes minimum) and DATABASE_URL
docker compose -f docker-compose.dev.yml up -d db     # PostgreSQL 16 on port 5433
alembic upgrade head
python manage.py create-admin --email admin@example.com --password '…' --display-name Admin
uvicorn app.main:app --reload
```

Tests: `pytest` runs on SQLite automatically; with `DATABASE_URL_TEST` set it runs on PostgreSQL and includes the
migration test. `ruff check . && ruff format --check .` before merging.

### The client

```bash
cd client
dotnet build Josour.sln
dotnet test Josour.sln
```

`Josour.App` is a single Avalonia interface that runs on both systems
([ADR-0013](docs/decisions/0013-avalonia-and-macos.md)). The project targets `net8.0` and
`net8.0-windows10.0.19041.0` together: the second one exists for Windows toast notifications and their buttons
alone, and everything else — every screen included — is built once.

```bash
dotnet run --project src/Josour.App -f net8.0 -- --mock   # the interface against a simulated server, no backend
dotnet test tests/Josour.App.Tests                          # the headless interface tests
```

**macOS status:** both roles work. As a guest the app points every enabled network service's proxy at itself with
`networksetup` for the length of a session (macOS may ask for an administrator's password) and puts it back afterwards
([ADR-0015](docs/decisions/0015-system-proxy-instead-of-work-browser.md)). The detail is in [docs/macos-port.md](docs/macos-port.md).

### The relay

```bash
cd relay
python3.12 -m venv .venv && source .venv/bin/activate
pip install -e ".[dev]"
pytest -q && ruff check . && ruff format --check .
RELAY_SECRET="$(openssl rand -base64 48)" RELAY_PORT=8443 python -m relay
```

Benchmarks: `pip install -e ".[bench]" && python -m bench` — detail in
[docs/performance-relay.md](docs/performance-relay.md).

## Tracks

| Track | Scope |
|---|---|
| A: the server | `backend/` |
| A: the server and the relay | `backend/`, `relay/` |
| B: networking | `client/src/Josour.{Core,Tunnel,Proxy,Egress}` + `client/tools/` |
| C: the application | `client/src/Josour.{Infrastructure,App}` |
| D: DevOps/QA | `deploy/`, `.github/`, `client/installer/`, `docs/runbook.md`, `docs/acceptance-checklist.md` |
