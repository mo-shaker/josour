#!/usr/bin/env bash
# متطلب الوثيقة 6.7 و14: حذف رموز ومفاتيح الجلسة بعد انتهائها.
# يفشل إن بقي صف واحد في session_keys لجلسة منتهية.
# الاستخدام: ./check-session-keys.sh [compose-project-dir]  (افتراضيًا deploy/)
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
  echo "FAIL: مفاتيح جلسات منتهية ما زالت مخزّنة (متطلب 6.7)" >&2
  docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c \
    "select k.session_id, s.ended_at, s.end_reason from session_keys k join sessions s on s.id = k.session_id where s.status='ended' limit 10;" >&2
  exit 1
fi
echo "PASS"
