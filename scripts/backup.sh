#!/usr/bin/env sh
# Create a compressed PostgreSQL backup (custom format) from the docker compose "db" service.
#   ./scripts/backup.sh                 -> backups/accounting-YYYYmmdd-HHMMSS.dump
#   ./scripts/backup.sh /path/to/file.dump
set -eu
cd "$(dirname "$0")/.."
DB="${POSTGRES_DB:-$(grep -E '^POSTGRES_DB=' .env 2>/dev/null | cut -d= -f2 || true)}"; DB="${DB:-accounting}"
USER="${POSTGRES_USER:-$(grep -E '^POSTGRES_USER=' .env 2>/dev/null | cut -d= -f2 || true)}"; USER="${USER:-accounting}"
OUT="${1:-backups/accounting-$(date +%Y%m%d-%H%M%S).dump}"
mkdir -p "$(dirname "$OUT")"
docker compose exec -T db pg_dump -U "$USER" -d "$DB" --format=custom --compress=9 --no-owner > "$OUT"
# Verify the archive is readable before reporting success.
docker compose exec -T db pg_restore --list < "$OUT" > /dev/null
echo "Backup written to $OUT ($(du -h "$OUT" | cut -f1))"
