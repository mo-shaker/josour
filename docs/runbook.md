# Runbook: running the Josour server on a VPS

The goal: a small Linux VPS (1 vCPU / 2 GB) running Caddy + FastAPI + PostgreSQL + a daily backup with Docker Compose,
and optionally the relay as a second, separate Compose stack. The step-by-step installation from an empty (or freshly
formatted) server, the apps and the first session are in [self-hosting.md](self-hosting.md); this file is the
operator's reference once it runs.

Paths below assume the repository is cloned at `/opt/josour` and every `docker compose` command runs from
`/opt/josour/deploy` with the default project name (`deploy`) — `backup/restore.sh`, `scripts/security/*` and the
staging workflow all assume it.

## 1. Preparing the server (once)

```bash
# Ubuntu 24.04 LTS, as root
apt update && apt -y upgrade
apt -y install ufw fail2ban unattended-upgrades ca-certificates curl git
ufw default deny incoming && ufw default allow outgoing
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp
ufw --force enable
dpkg-reconfigure -plow unattended-upgrades
curl -fsSL https://get.docker.com | sh
```

- Create an `A` record for `josour.example.com` pointing at the server's IP before the first run (Caddy needs it to
  issue the certificate). No `AAAA`: with Docker's default network, IPv6 visitors reach Caddy through Docker's
  userland proxy and the API sees the Docker gateway's address for all of them.
- SSH by key only (`PasswordAuthentication no` in `sshd_config`).
- **Docker-published ports bypass ufw.** Caddy's 80/443 and the relay's port are opened by Docker's own iptables
  rules: `ufw allow` neither opens nor closes them. ufw protects SSH and anything outside Docker. A cloud provider's
  firewall does apply and must allow 80, 443 (TCP and UDP) and the relay's port.

## 2. The first deployment

```bash
git clone https://github.com/mo-shaker/josour.git /opt/josour && cd /opt/josour/deploy
cp .env.example .env && chmod 600 .env
# required: DOMAIN, ACME_EMAIL, POSTGRES_PASSWORD and JWT_SECRET (secrets: openssl rand -hex 32)
nano .env
docker compose up -d --build
docker compose ps                     # wait for api "(healthy)"
# the first administrator (asks for the password)
docker compose exec api python manage.py create-admin --email you@example.com --display-name "You"
curl -s https://josour.example.com/healthz     # the health check, outside /api/v1 and unauthenticated
```

`docker compose` refuses to start while a required value is empty. `POSTGRES_PASSWORD` is fixed when the database
volume is created on the first start; changing it in `.env` afterwards only locks the API out.

The dashboard is at `https://josour.example.com/admin` (ADR-0016). Runtime settings — registration mode, the oldest
app version accepted, the allow-list switch, allowed ports, session limits — live in the database and are edited
there (*Settings*) or with `PATCH /api/v1/admin/settings`; `.env` holds only what the process needs at start-up.

The relay (ADR-0009) is a separate stack with its own env file and project name, never `.env` and never the default
project:

```bash
cp .env.relay.example .env.relay && chmod 600 .env.relay     # RELAY_SECRET = the API's; RELAY_PUBLIC_PORT=8443 beside the API
docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml up -d --build
```

and `RELAY_HOST`, `RELAY_PORT`, `RELAY_SECRET` in `.env`, then `docker compose up -d`. Details:
[self-hosting.md part 3](self-hosting.md#3-the-relay--what-makes-a-session-work-with-no-router-configuration).

## 3. Automatic deployment to staging

`.github/workflows/deploy-staging.yml` deploys automatically when `ci-backend` succeeds on `main`, or by hand from the
Actions tab. Until its secrets exist, an automatic run skips with a notice.

The secrets required in the repository's settings (Settings → Secrets → Actions), under an environment named
`staging`:

| Secret | Contents |
|---|---|
| `STAGING_HOST` | The server's address or name |
| `STAGING_USER` | The SSH user |
| `STAGING_SSH_KEY` | A private ed25519 key with no passphrase, with its counterpart in `authorized_keys` on the server |
| `STAGING_PATH` | The repository's path on the server, e.g. `/opt/josour` |

The steps it performs: a backup before any change → `git reset --hard origin/main` → rebuilding `api` → waiting for
`/healthz` for up to 150 seconds → running two security scripts (session keys and log cleanliness) → **an automatic
rollback to the previous version on any failure**.

The server must already be prepared per section 1 with `deploy/.env` in place; the workflow does not create the
environment from scratch, and it does not touch the relay stack.

## 4. Upgrading

```bash
cd /opt/josour && git pull
cd deploy
docker compose exec backup /usr/local/bin/backup.sh     # a backup first
docker compose up -d --build
docker compose logs --tail=50 api
# if the relay runs on this machine:
docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml up -d --build
docker image prune -f
```

Migrations are applied automatically when `api` starts (`alembic upgrade head`). Restarting `api` drops every
WebSocket connection; clients reconnect with exponential backoff and active sessions are ended (per the contract), so
upgrade when nobody is in a session.

A change to `.env` is applied with `docker compose up -d`; `docker compose restart` does not re-read `.env`.

A relay started before 2.0 without `-p` ran in the `deploy` project as `deploy-relay-1` and still holds its port;
remove it once (`docker rm -f deploy-relay-1`) before starting it as `josour-relay`.

## 5. Backup and restore

- Automatic: the `backup` service writes `deploy/backups/josour-<UTC>.sql.gz` when it starts and every 24 hours after,
  and deletes those older than `BACKUP_RETENTION_DAYS`.
- Copy the directory off the server (rsync or S3) daily; a copy on the same disk is not a disaster plan. Keep a copy
  of `deploy/.env` and `deploy/.env.relay` off the server as well.
- Restoring: `./backup/restore.sh backups/josour-….sql.gz` (stops `api`, drops and recreates the database with
  `WITH (FORCE)`, imports with `ON_ERROR_STOP`, starts `api`, which migrates an older dump forward).
- Test the restore on the staging server once a month.

### The restore exercise's results

**2026-09-05.** The full cycle on a clean environment: starting the stack, creating an administrator, a user and a
site list, taking a backup, deliberately deleting every row, then `restore.sh`.

| Check | Result |
|---|---|
| The script finished with no error | ✅ |
| The user, site and list-version rows | ✅ came back complete |
| Signing in with a restored account | ✅ works |
| The migration state (`alembic_version`) | ✅ sound |
| `/healthz` after the restore | ✅ 200 |

**2026-10-21**, as part of a from-scratch install test with Docker (fresh volumes, `DOMAIN=localhost`): backup,
an account created *after* the backup, then `restore.sh`. The later account was gone, the earlier ones signed in,
`alembic_version` was at head and `/healthz` answered 200.

**An operational note:** the script calls `docker compose` with the default project name derived from the directory's
name (`deploy`). If the stack was started under a custom project name (`-p`), export the same `COMPOSE_PROJECT_NAME`
when restoring.

## 6. Monitoring and logs

```bash
docker compose ps
docker compose logs --since 1h api
docker compose exec db psql -U josour -d josour -c "select status, count(*) from sessions group by 1;"
docker compose -p josour-relay --env-file .env.relay -f docker-compose.relay.yml logs --since 1h relay
```

(`josour` is the default `POSTGRES_USER`/`POSTGRES_DB`; use the values in your `.env` if they differ.)

- `api` runs with a single worker deliberately (the WebSocket connection registry is in memory). Do not raise
  `--workers` before adding Redis Pub/Sub.
- The API's access log shows the client address it sees for each request — the one `ADMIN_WEB_ALLOWED_CIDRS` and the
  rate limits are checked against.

## 7. Security

- `/docs` and `/openapi.json` are served only to an administrator's bearer token when `ENV` is not `dev` (the
  production compose sets `ENV=prod`); in `dev` they are open.
- The dashboard (`/admin`): turn on `ADMIN_WEB_REQUIRE_TOTP=true` once every administrator has enrolled, and restrict
  it with `ADMIN_WEB_ALLOWED_CIDRS` (or the Caddyfile's commented block) where the administrators' addresses are
  stable. `ADMIN_WEB_ENABLED=false` removes it altogether.
- A lost authenticator: `docker compose exec db psql -U josour -d josour -c "update users set totp_secret = null,
  totp_enabled_at = null, totp_last_step = null where email = 'you@example.com';"`.
- Do not open PostgreSQL's port to the outside; access is through `docker compose exec db psql` only.
- `JWT_SECRET` signs access tokens only (15 minutes); rotating it invalidates every access token at once.
- `RELAY_SECRET` is separate from `JWT_SECRET` on purpose and must match byte for byte between `.env` and
  `.env.relay`.
- The relay is **not** behind Caddy: it is its own stack (`docker-compose.relay.yml`), publishing plain TCP on its
  own port — 8443 beside the API, or 443 on a host of its own. Nothing terminates TLS in front of it; the two apps'
  TLS runs inside its stream.
