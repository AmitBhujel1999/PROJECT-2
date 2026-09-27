# Operations: deployment, migrations, backup & restore

## Production deployment

1. **Server**: any Linux host with Docker Engine + Compose v2, a DNS name, and
   ports 80/443 open.
2. **Configuration** — `cp .env.example .env` and set at least:

   | Variable | Production value |
   |---|---|
   | `DEBUG` | `False` |
   | `SECRET_KEY` | 50+ random characters (`python -c "import secrets;print(secrets.token_urlsafe(64))"`) |
   | `POSTGRES_PASSWORD` | strong random password (also used in `DATABASE_URL`) |
   | `ALLOWED_HOSTS` | `accounting.example.com` |
   | `CSRF_TRUSTED_ORIGINS` | `https://accounting.example.com` |
   | `CORS_ALLOWED_ORIGINS` | leave empty or your domain (same-origin needs no CORS) |
   | `FRONTEND_URL` | `https://accounting.example.com` (password-reset links) |
   | `USE_HTTPS` | `True` (secure cookies, HSTS, SSL redirect) |
   | `DJANGO_SUPERUSER_*` | first admin; change the password after first login |
   | `EMAIL_*` | SMTP settings for password reset |
   | `LOAD_DEMO_DATA` | `False` |

3. **TLS**: terminate HTTPS in front of the `nginx` service (e.g. a host-level
   Caddy/Traefik/nginx with Let's Encrypt, or a cloud load balancer) and forward
   to `HTTP_PORT`, passing `X-Forwarded-Proto: https`. Django trusts that header
   for secure-request detection when `USE_HTTPS=True`.
4. **Start**: `docker compose up -d --build`. The backend entrypoint waits for
   PostgreSQL, runs migrations, creates the first admin (only if there are no
   users) and starts gunicorn.
5. **Verify**: `docker compose ps` (all healthy),
   `docker compose exec backend python manage.py check --deploy` (no issues
   with `USE_HTTPS=True`), log in, check the audit log.
6. **Upgrade**: `git pull && docker compose up -d --build` — migrations run
   automatically. Take a backup first.

Only nginx publishes a port; PostgreSQL and the backend are reachable only on
the internal compose network.

## Database migrations

* Migrations are committed in each app's `backend/apps/<app>/migrations/`.
* After changing a model: `uv run python manage.py makemigrations <app>`, review
  the file, run `uv run pytest`, commit.
* CI/local guard: `uv run python manage.py makemigrations --check --dry-run`
  must print "No changes detected".
* Apply: automatically on container start, or `uv run python manage.py migrate`.
* Hand-written migrations: `common/0002_seed_sequences_and_settings` (document
  number sequences + settings row) and `inventory/0002_immutable_ledger_trigger`
  (PostgreSQL trigger forbidding UPDATE/DELETE on the stock ledger). Keep these
  when squashing.
* Financial tables are append-only by design; never write data migrations that
  update or delete historical documents — add reversing records instead.

## Backup

```bash
./scripts/backup.sh                      # -> backups/accounting-YYYYmmdd-HHMMSS.dump
./scripts/backup.sh /mnt/backups/x.dump  # custom location
```

Equivalent manual commands:

```bash
docker compose exec -T db pg_dump -U accounting -d accounting \
  --format=custom --compress=9 --no-owner > backup.dump
docker compose exec -T db pg_restore --list < backup.dump > /dev/null   # verify
```

Outside Docker: `pg_dump -h HOST -U accounting -d accounting -Fc -f backup.dump`.

### Recommended frequency

| What | When | Keep |
|---|---|---|
| Full `pg_dump` | daily (e.g. 01:00 via cron) | 30 daily, 12 monthly |
| Before every upgrade / migration | always | until the upgrade is verified |
| Off-site copy (object storage, encrypted) | daily | per retention policy |
| Restore test into a scratch environment | monthly | — |

Example cron entry (host):
`0 1 * * * cd /opt/accounting-system && ./scripts/backup.sh >> backups/backup.log 2>&1`

For point-in-time recovery on busy systems, additionally enable PostgreSQL WAL
archiving (e.g. pgBackRest or wal-g) — `pg_dump` alone restores to the moment
of the dump.

## Restore

```bash
./scripts/restore.sh backups/accounting-20260927-010000.dump
# asks for confirmation; FORCE=1 skips the prompt
```

The script stops backend/frontend/nginx, runs
`pg_restore --clean --if-exists --no-owner --single-transaction`, and starts the
stack again. Manual equivalent:

```bash
docker compose stop backend frontend nginx
docker compose exec -T db pg_restore -U accounting -d accounting \
  --clean --if-exists --no-owner --single-transaction < backup.dump
docker compose start backend frontend nginx
```

Outside Docker: `pg_restore -h HOST -U accounting -d accounting --clean --if-exists --no-owner backup.dump`.

## Environment recovery (new server)

1. Install Docker, clone the repository at the same release tag/commit.
2. Restore the `.env` file from your secrets store (the `SECRET_KEY` must match
   to keep existing sessions/password-reset tokens valid; changing it only logs
   everyone out).
3. `docker compose up -d db` and wait until healthy.
4. `FORCE=1 ./scripts/restore.sh <latest.dump>` (this also starts the app containers).
5. `docker compose up -d --build` — pending migrations (if the code is newer than
   the dump) apply automatically.
6. Verify: log in, compare dashboard totals / latest invoice numbers with the
   backup time, check `docker compose logs backend`.

## Monitoring & logs

* `docker compose logs -f backend` — gunicorn access log + application errors.
* `GET /api/health/` — liveness (used by the container healthcheck).
* The audit log (`/settings/audit`) records every financial change, login and
  failed login with IP address.
