#!/bin/sh
set -eu
schedule="${BACKUP_CRON:-0 2 * * *}"
echo "$schedule /usr/local/bin/backup.sh >> /proc/1/fd/1 2>> /proc/1/fd/2" > /etc/crontabs/root
echo "PostgreSQL backup scheduler started: $schedule UTC"
exec crond -f -l 8
