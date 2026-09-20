# Josour

A desktop application for Windows and macOS that lets one person (the guest) use another person's internet
connection (the host's) temporarily and with their explicit consent, so that sites which only work from inside the
host's country become reachable — an expatriate who needs their bank or their government's portal. Everything the
work browser asks for leaves from the host's address ([ADR-0010](docs/decisions/0010-route-all-through-host.md)),
and nothing else on either machine is affected.

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
> What does **not** happen: your other applications are unaffected, your local network and `localhost` services
> stay out of reach, and the guest goes through an isolated work browser and nothing else.

A host can spare themselves the question every time by putting particular guests on a trusted list, after which
their requests are accepted at once with no window shown
([ADR-0012](docs/decisions/0012-auto-accept-trusted-guests.md)). The consent is still the host's — given in
advance rather than at each request — capped by duration, expiring on its own, and withdrawable at any moment.

User management — creating accounts, disabling them, unlocking them, setting passwords — happens **inside the
application** for anyone whose role is `admin`, not only from the command line. There is no web panel: an
administrative panel on the internet would be the highest-value target in the system, and this one exposes
nothing.

Browsing data is encrypted end to end between the two machines: **the server cannot read it**. When a direct
connection is impossible — which is the normal case between two mobile networks — the opaque bytes travel through
a relay that does not understand them ([ADR-0009](docs/decisions/0009-relay-default.md)), so a session works with
no router or firewall configuration at all.

## Layout

| Directory | Contents |
|---|---|
| `backend/` | Control server: Python 3.12 + FastAPI + PostgreSQL + WebSockets |
| `client/` | .NET 8 solution: tunnel, proxy, egress and browser libraries + the Avalonia app ([ADR-0013](docs/decisions/0013-avalonia-and-macos.md)) |
| `relay/` | Relay service: pairs the two ends of a session and pumps opaque bytes ([ADR-0009](docs/decisions/0009-relay-default.md)) |
| `deploy/` | Production Docker Compose (Caddy + API + PostgreSQL + backups) and a **standalone relay** |
| `docs/` | Implementation plan, the frozen contracts (REST / WebSocket / channel protocol), ADRs, runbook |

## Install it and use it

**[docs/self-hosting.md](docs/self-hosting.md)** is the complete guide: from an empty VPS to a running server, then
the app on two machines, then a session you can verify.

**There is a Windows app and a macOS app**, and they are the same application — one codebase, one interface, both
roles (host and guest) on both systems ([ADR-0013](docs/decisions/0013-avalonia-and-macos.md)):

| | Windows | macOS |
|---|---|---|
| Build it | `scripts\publish-exe.ps1` → one `Josour.exe` | `scripts/publish-app.sh --dmg` → `Josour.app` + a `.dmg` |
| Needs .NET installed on the user's machine? | No | No |
| First launch, once per machine | SmartScreen: **More info → Run anyway** | Gatekeeper: **right-click → Open → Open** |

Both are unsigned by decision ([ADR-0011](docs/decisions/0011-no-code-signing-certificate.md)); the Windows build
prints a SHA-256 to check the file by.

**Status:** a full session between two machines on different networks has been working since 2026-09-09 (milestone
M2). Latest report: [docs/status-week7.md](docs/status-week7.md).

The full plan: [docs/Josour-MVP-Implementation-Plan.md](docs/Josour-MVP-Implementation-Plan.md)

## Where this project stands, honestly

One person maintains it, and it is published as a contribution to the community. **No support, no warranty, and no
contributions accepted** — issues and pull requests are closed, and that is honesty rather than rudeness: accepting
a change to a tool that carries other people's traffic means reading every line for whether it quietly widens the
egress policy, and I do not have the time to do that properly. Fork it and change it as you like
([Apache-2.0](LICENSE)).

**Nobody runs a public relay.** Anyone who wants to use this hosts their own server and relay; the complete guide
is [docs/self-hosting.md](docs/self-hosting.md).

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

**macOS status:** both roles work. The connection-owner check — the control that keeps the proxy serving the work
browser alone — is implemented with `lsof`, and the browser is launched from inside its bundle with ownership read
from the process tree. The detail is in [docs/macos-port.md](docs/macos-port.md).

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
| B: networking | `client/src/Josour.{Core,Tunnel,Proxy,Egress,Browser}` + `client/tools/` |
| C: the application | `client/src/Josour.{Infrastructure,App}` |
| D: DevOps/QA | `deploy/`, `.github/`, `client/installer/`, `docs/runbook.md`, `docs/acceptance-checklist.md` |
