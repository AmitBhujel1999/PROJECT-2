#!/bin/sh
set -e

# Wait for PostgreSQL
echo "Waiting for database..."
i=0
until python -c "import psycopg, os; psycopg.connect(os.environ['DATABASE_URL']).close()" 2>/dev/null; do
    i=$((i + 1))
    if [ "$i" -gt 60 ]; then
        echo "Database not reachable after 60s" >&2
        exit 1
    fi
    sleep 1
done

python manage.py migrate --noinput
python manage.py ensure_admin
if [ "${LOAD_DEMO_DATA:-False}" = "True" ]; then
    python manage.py seed_demo_data --if-empty
fi

exec "$@"
