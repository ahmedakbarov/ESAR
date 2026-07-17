#!/bin/sh
set -eu
: "${POSTGRES_HOST:?}" "${POSTGRES_DB:?}" "${POSTGRES_USER:?}" "${POSTGRES_PASSWORD:?}"
mkdir -p /data/backups
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
file="/data/backups/${POSTGRES_DB}-${stamp}.sql.gz"
echo "[$(date -u +%FT%TZ)] starting PostgreSQL backup"
PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -h "$POSTGRES_HOST" -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-privileges | gzip -c > "$file"
test -s "$file"
find /data/backups -type f -name '*.sql.gz' -mtime +"${BACKUP_RETENTION_DAYS:-7}" -print -delete
echo "[$(date -u +%FT%TZ)] backup complete: $file"
