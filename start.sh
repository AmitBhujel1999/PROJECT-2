#!/usr/bin/env sh
# One-command start for macOS / Linux:  ./start.sh
# Creates .env with random secrets on first run, then starts the whole stack.
set -eu
cd "$(dirname "$0")"

if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
    echo "Docker is not installed or not running. Install/start Docker Desktop first:"
    echo "  https://www.docker.com/products/docker-desktop"
    exit 1
fi

if [ ! -f .env ]; then
    rand() { LC_ALL=C tr -dc 'A-Za-z0-9' < /dev/urandom | head -c "$1"; }
    DB_PASS=$(rand 24)
    ADMIN_PASS="Admin-$(rand 10)"
    PORT="${HTTP_PORT:-80}"
    if [ "$PORT" = "80" ]; then ORIGIN="http://localhost"; ORIGINS="http://localhost,http://127.0.0.1"
    else ORIGIN="http://localhost:$PORT"; ORIGINS="http://localhost:$PORT,http://127.0.0.1:$PORT"; fi
    sed -e "s|^SECRET_KEY=.*|SECRET_KEY=$(rand 64)|" \
        -e "s|CHANGE-ME-db-password|$DB_PASS|g" \
        -e "s|^DJANGO_SUPERUSER_PASSWORD=.*|DJANGO_SUPERUSER_PASSWORD=$ADMIN_PASS|" \
        -e "s|^LOAD_DEMO_DATA=.*|LOAD_DEMO_DATA=${LOAD_DEMO_DATA:-True}|" \
        -e "s|^HTTP_PORT=.*|HTTP_PORT=$PORT|" \
        -e "s|^CSRF_TRUSTED_ORIGINS=.*|CSRF_TRUSTED_ORIGINS=$ORIGINS|" \
        -e "s|^FRONTEND_URL=.*|FRONTEND_URL=$ORIGIN|" \
        .env.example > .env
    echo "Created .env with random secrets."
fi

docker compose up -d --build

PORT=$(grep -E '^HTTP_PORT=' .env | cut -d= -f2); PORT=${PORT:-80}
URL="http://localhost"; [ "$PORT" = "80" ] || URL="http://localhost:$PORT"
echo "Waiting for the app to become ready..."
i=0
until curl -fs "$URL/api/health/" >/dev/null 2>&1; do
    i=$((i + 1)); [ "$i" -gt 120 ] && { echo "Not ready after 4 minutes; check: docker compose logs backend"; exit 1; }
    sleep 2
done

echo ""
echo "============================================================"
echo " Accounting & Inventory is running:  $URL"
echo " Username: $(grep -E '^DJANGO_SUPERUSER_USERNAME=' .env | cut -d= -f2)"
echo " Password: $(grep -E '^DJANGO_SUPERUSER_PASSWORD=' .env | cut -d= -f2)"
echo " (stored in .env - change it after first login via Profile)"
echo " Stop with: docker compose down"
echo "============================================================"
