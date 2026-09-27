#!/usr/bin/env sh
# Restore a backup made by backup.sh into the docker compose "db" service.
# WARNING: replaces all data in the target database.
#   ./scripts/restore.sh backups/accounting-20260927-101500.dump
set -eu
cd "$(dirname "$0")/.."
FILE="${1:?Usage: scripts/restore.sh <backup.dump>}"
[ -f "$FILE" ] || { echo "No such file: $FILE" >&2; exit 1; }
DB="${POSTGRES_DB:-$(grep -E '^POSTGRES_DB=' .env 2>/dev/null | cut -d= -f2 || true)}"; DB="${DB:-accounting}"
USER="${POSTGRES_USER:-$(grep -E '^POSTGRES_USER=' .env 2>/dev/null | cut -d= -f2 || true)}"; USER="${USER:-accounting}"

if [ "${FORCE:-}" != "1" ]; then
    printf "This will REPLACE all data in database '%s'. Type 'restore' to continue: " "$DB"
    read -r answer
    [ "$answer" = "restore" ] || { echo "Aborted."; exit 1; }
fi

echo "Stopping application containers..."
docker compose stop backend frontend nginx >/dev/null 2>&1
docker compose exec -T db pg_restore -U "$USER" -d "$DB" --clean --if-exists --no-owner --single-transaction < "$FILE"
echo "Starting application containers..."
docker compose start backend frontend nginx >/dev/null 2>&1
echo "Restore complete from $FILE"
