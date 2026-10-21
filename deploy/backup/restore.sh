#!/bin/sh
# Restore a backup: ./backup/restore.sh backups/josour-YYYYMMDDTHHMMSSZ.sql.gz
# It stops api, recreates the database, imports, then starts api (which applies any newer
# migrations on start). Run it from deploy/ or anywhere: it changes to deploy/ itself.
# It uses the default compose project ("deploy"); export COMPOSE_PROJECT_NAME if yours differs.
set -eu
FILE="${1:?usage: restore.sh <file.sql.gz>}"
case "$FILE" in /*) ;; *) FILE="$(pwd)/$FILE" ;; esac
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
POSTGRES_USER="${POSTGRES_USER:-josour}"
POSTGRES_DB="${POSTGRES_DB:-josour}"
docker compose stop api
docker compose exec -T db psql -U "$POSTGRES_USER" -d postgres -v ON_ERROR_STOP=1 \
  -c "DROP DATABASE IF EXISTS \"${POSTGRES_DB}\" WITH (FORCE);" -c "CREATE DATABASE \"${POSTGRES_DB}\";"
gunzip -c "$FILE" | docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q -v ON_ERROR_STOP=1
docker compose start api
echo "restored from $FILE"
