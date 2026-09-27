# syntax=docker/dockerfile:1
#
# One Dockerfile, two application images (selected with --target):
#   backend  - Django + Gunicorn, dependencies installed with uv
#   frontend - SvelteKit (adapter-node) built and served with Bun
#
# docker compose builds both targets; see docker-compose.yml.
#
# Behind a TLS-intercepting corporate proxy, pass its CA certificate as the
# optional build secret "extra_ca" (EXTRA_CA_CERT=/path/ca.crt in .env);
# without it the step below is a no-op.

# ---------------------------------------------------------------------------
# Backend
# ---------------------------------------------------------------------------
FROM python:3.11-slim AS backend-base
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    UV_PROJECT_ENVIRONMENT=/opt/venv \
    PATH="/opt/venv/bin:$PATH" \
    SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    PIP_CERT=/etc/ssl/certs/ca-certificates.crt \
    UV_NATIVE_TLS=1
RUN --mount=type=secret,id=extra_ca,required=false \
    if [ -s /run/secrets/extra_ca ]; then \
      cp /run/secrets/extra_ca /usr/local/share/ca-certificates/extra-ca.crt && update-ca-certificates; \
    fi \
    && pip install --no-cache-dir "uv==0.8.17"
WORKDIR /app

FROM backend-base AS backend
COPY backend/pyproject.toml backend/uv.lock ./
RUN uv sync --frozen --no-dev --no-install-project
COPY backend/ ./
RUN chmod +x docker/entrypoint.sh \
    && DEBUG=False SECRET_KEY=build-only-collectstatic DATABASE_URL=postgresql://x:x@localhost/x \
       python manage.py collectstatic --noinput \
    && useradd --system --uid 1001 --home /app appuser && chown -R appuser /app
USER appuser
EXPOSE 8000
ENTRYPOINT ["/app/docker/entrypoint.sh"]
CMD ["gunicorn", "config.wsgi:application", "--bind", "0.0.0.0:8000", "--workers", "3", "--timeout", "60", "--access-logfile", "-"]

# ---------------------------------------------------------------------------
# Frontend
# ---------------------------------------------------------------------------
FROM oven/bun:1.3.11 AS frontend-build
WORKDIR /app
COPY frontend/package.json frontend/bun.lock ./
RUN --mount=type=secret,id=extra_ca,required=false \
    if [ -s /run/secrets/extra_ca ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/extra_ca; fi \
    && bun install --frozen-lockfile
COPY frontend/ ./
RUN bun run build

FROM oven/bun:1.3.11-slim AS frontend
WORKDIR /app
ENV NODE_ENV=production PORT=3000 HOST=0.0.0.0
COPY --from=frontend-build /app/build ./build
COPY --from=frontend-build /app/package.json ./package.json
USER bun
EXPOSE 3000
CMD ["bun", "build/index.js"]
