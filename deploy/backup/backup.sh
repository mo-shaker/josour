#!/bin/sh
# نسخة احتياطية يومية مضغوطة مع حذف ما يتجاوز مدة الاحتفاظ.
set -eu
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
OUT="/backups/josour-${STAMP}.sql.gz"
mkdir -p /backups
pg_dump --no-owner --no-privileges | gzip -9 > "${OUT}.tmp" && mv "${OUT}.tmp" "${OUT}"
echo "backup written: ${OUT}"
find /backups -name 'josour-*.sql.gz' -mtime +"${BACKUP_RETENTION_DAYS:-14}" -delete
