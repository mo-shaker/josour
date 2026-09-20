#!/bin/sh
# Restore a backup: ./backup/restore.sh backups/josour-YYYYMMDDTHHMMSSZ.sql.gz
# It stops api, recreates the database, imports, then starts api.
set -eu
FILE="${1:?usage: restore.sh <file.sql.gz>}"
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
docker compose stop api
docker compose exec -T db psql -U "$POSTGRES_USER" -d postgres -c "DROP DATABASE IF EXISTS ${POSTGRES_DB};" -c "CREATE DATABASE ${POSTGRES_DB};"
gunzip -c "$FILE" | docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q
docker compose start api
echo "restored from $FILE"
