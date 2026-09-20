# Week 1 status (2026-09-04)

## What was completed and verified

| Track | Output | Verification |
|---|---|---|
| D | The repository (git initialised with no commits), README, `.gitignore`, `.editorconfig`, CI for the server (Postgres + ruff + pytest + docker build) and for the client (Windows + Linux), `deploy/` (Compose + Caddy + backup and restore), `docs/runbook.md`, `docs/acceptance-checklist.md`, `client/installer/Josour.iss` with the firewall rule | `docker compose config` and Caddy validate both pass |
| Contracts | `docs/api.md`, `docs/ws-protocol.md`, `docs/protocol.md`, six ADRs, the `Josour.Core` interfaces (IControlChannel, ITunnelSession, ITunnelTransport, IBrowserSession, ISecretStore, the WS messages) | Frozen; changes go through review by both sides |
| A | The server's core: the models complete (13 tables) + the Alembic migration, argon2id + JWT + refresh rotation with reuse detection, auth/me/hosts/domains/probe/healthz, account lockout, `security_events`, the CLI (create-admin, create-user, add-domain, list-domains), the Dockerfile | ruff clean; 77 tests + 1 skipped on SQLite; 78 on PostgreSQL 16 including the migration and `alembic check`; the image builds |
| B | `Core`: AllowlistMatcher, IpRangePolicy. `Tunnel`: SessionCertificate, TlsChannel (pinning + a 1.2 floor), AuthHandshake (AUTH1/AUTH2), CandidateGatherer (Mono.Nat 3.0.4), DirectTransport, TunnelListener, SymmetricConnector, HostDiagnostics. `Spike`: certtest / gather / symmetric / probe | 176 Core tests + 35 Tunnel; `certtest` works; a real symmetric connection between two processes within ~50 ms |
| C | `App`: a single instance, `--minimized`, Generic Host + Serilog, a main window with the host and user pages, a tray icon with a menu, a toast with accept/reject buttons, a top-most incoming-request window with a 60-second counter, start with Windows, app.manifest PerMonitorV2. `Infrastructure`: DpapiSecretStore, DeviceInfoProvider, LoggingSetup | A Release build of the whole solution with no errors and no warnings; 26 Infrastructure tests |

## ADR decisions
- 0002 (TLS), 0003 (NAT/relay), 0004 (routing) and 0005 (the installer): accepted.
- 0001 (WPF): the prototype is built and will be accepted after QA on real Windows.
- 0006 (mux): decided in week 2.

## What needs human intervention now
1. **Request the code-signing certificate (OV/EV)**, because issuance takes days.
2. **Prepare a staging VPS** per `docs/runbook.md` and deploy the server on it.
3. **Run the technical prototype on Windows** per `docs/spike-runbook.md`: `certtest` on Win10 and Win11, `gather` on
   two or three routers, then start the ten pairs.
4. **QA the WPF application on Windows** (tray, toast, single instance, start on login, hiding to the tray, DPI) to
   accept ADR-0001.

## Week 2 (from the plan)
- A: `admin_*`, rate limiting, `GET /sessions/me`, the additional integration tests. (Note: `/docs` is currently only
  in `ENV=dev`; restricting it to administrators in production is part of week 2.)
- B: the Nerdbank prototype against hand-written framing → ADR-0006; an initial proxy with the check page; the browser
  launch matrix; gathering the ten pairs' data.
- C: `ApiClient` + the sign-in screen against staging; `MockControlChannel` from `ws-protocol.md`; the
  `--uninstall-notifications` switch.
- D: deploying staging, checking SmartScreen on the signed installer, updating the acceptance list.
