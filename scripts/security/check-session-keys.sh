#!/usr/bin/env bash
# Document requirement 6.7 and 14: the session's tokens and keys are deleted once it ends.
# It fails if one row is left in session_keys for an ended session.
# Usage: ./check-session-keys.sh [compose-project-dir]  (deploy/ by default)
set -euo pipefail
DIR="${1:-$(cd "$(dirname "$0")/../../deploy" && pwd)}"
cd "$DIR"
set -a; . ./.env; set +a

orphans=$(docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
  "select count(*) from session_keys k join sessions s on s.id = k.session_id where s.status = 'ended';")
live=$(docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
  "select count(*) from session_keys;")

echo "session_keys rows total: $live"
echo "session_keys rows for ENDED sessions: $orphans"
if [ "$orphans" != "0" ]; then
  echo "FAIL: keys for ended sessions are still stored (requirement 6.7)" >&2
  docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
    "select k.session_id, s.ended_at, s.end_reason from session_keys k join sessions s on s.id = k.session_id where s.status='ended' limit 10;" >&2
  exit 1
fi
echo "PASS"
