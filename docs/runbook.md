# Runbook: running the Josour server on a VPS

The goal: a small Linux VPS (1 vCPU / 2 GB) running Caddy + FastAPI + PostgreSQL + a daily backup with Docker Compose.

## 1. Preparing the server (once)

```bash
# Ubuntu 24.04 LTS
apt update && apt -y upgrade
apt -y install ufw fail2ban unattended-upgrades ca-certificates curl
ufw default deny incoming && ufw default allow outgoing
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp
ufw --force enable
dpkg-reconfigure -plow unattended-upgrades
curl -fsSL https://get.docker.com | sh
```

- Create an A record for `rb.example.com` pointing at the server's IP before the first run (Caddy needs it to issue the
  certificate).
- SSH by key only (`PasswordAuthentication no` in `sshd_config`).

## 2. The first deployment

```bash
git clone <repo> /opt/routebridge && cd /opt/routebridge/deploy
cp .env.example .env
# set DOMAIN, ACME_EMAIL, the PostgreSQL password and JWT_SECRET (openssl rand -base64 48)
docker compose up -d --build
docker compose logs -f api   # wait for: "Application startup complete"
# the first administrator
docker compose exec api python manage.py create-admin --email admin@example.com --password '…' --display-name "Admin"
curl -s https://rb.example.com/healthz     # the health check, outside /api/v1 and unauthenticated
```

## 3. Automatic deployment to staging

`.github/workflows/deploy-staging.yml` deploys automatically when `ci-backend` succeeds on `main`, or by hand from the
Actions tab.

The secrets required in the repository's settings (Settings → Secrets → Actions), under an environment named
`staging`:

| Secret | Contents |
|---|---|
| `STAGING_HOST` | The server's address or name |
| `STAGING_USER` | The SSH user |
| `STAGING_SSH_KEY` | A private ed25519 key with no passphrase, with its counterpart in `authorized_keys` on the server |
| `STAGING_PATH` | The repository's path on the server, e.g. `/opt/routebridge` |

The steps it performs: a backup before any change → `git reset --hard origin/main` → rebuilding `api` → waiting for
`/healthz` for up to 150 seconds → running two security scripts (session keys and log cleanliness) → **an automatic
rollback to the previous version on any failure**.

The server must already be prepared per section 1 with `deploy/.env` in place; the workflow does not create the
environment from scratch.

## 4. Upgrading

```bash
cd /opt/routebridge && git pull
cd deploy && docker compose build api && docker compose up -d api
docker compose logs --tail=50 api
```
Migrations are applied automatically when `api` starts (`alembic upgrade head`). For a risky migration: take a manual
backup first (`docker compose exec backup /usr/local/bin/backup.sh`).

## 5. Backup and restore

- Automatic: the `backup` service writes `deploy/backups/josour-<UTC>.sql.gz` daily and deletes anything older than
  `BACKUP_RETENTION_DAYS`.
- Copy the directory off the server (rsync or S3) daily; a copy on the same disk is not a disaster plan.
- Restoring: `./backup/restore.sh backups/josour-….sql.gz` (stops `api`, recreates the database, imports, starts `api`).
- Test the restore on the staging server once a month.

### The restore exercise's result (2026-09-05)

The full cycle was exercised on a clean environment: starting the stack, creating an administrator, a user and a site
list, taking a backup, deliberately deleting every row, then `restore.sh`.

| Check | Result |
|---|---|
| The script finished with no error | ✅ |
| The user, site and list-version rows | ✅ came back complete |
| Signing in with a restored account | ✅ works |
| The migration state (`alembic_version`) | ✅ sound |
| `/healthz` after the restore | ✅ 200 |

**An operational note:** the script calls `docker compose` with the default project name derived from the directory's
name (`deploy`). If the stack was started under a custom project name (`-p`), pass the same `COMPOSE_PROJECT_NAME` when
restoring.

## 6. Monitoring and logs

```bash
docker compose ps
docker compose logs --since 1h api
docker compose exec db psql -U routebridge -c "select status, count(*) from sessions group by 1;"
```
- `api` runs with a single worker deliberately (the WebSocket connection registry is in memory). Do not raise
  `--workers` before adding Redis Pub/Sub.
- Restarting `api` drops every WebSocket connection; clients reconnect with exponential backoff and active sessions are
  ended (per the contract).

## 7. Security

- `/docs` is disabled with `ENV=prod`.
- Do not open PostgreSQL's port to the outside; access is through `docker compose exec db psql` only.
- Rotate `JWT_SECRET` only alongside a planned mass sign-out (it invalidates every access token at once).
- If a relay is added later: a separate service behind Caddy with its own SNI on 443.
