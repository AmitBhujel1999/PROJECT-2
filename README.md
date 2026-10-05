# Accounting & Inventory Management System

A production-oriented accounting and inventory platform for real business
operations (Nepal-ready: NPR, PAN/VAT, 13 % VAT by default — all configurable).

| Layer | Technology |
|---|---|
| Database | PostgreSQL 16 (authoritative; `NUMERIC` for every financial value) |
| Backend | Python 3.11, Django 5.2, Django REST Framework, managed with **uv** |
| Frontend | SvelteKit (Svelte 5, TypeScript), Tailwind CSS v4, bits-ui (shadcn-style), Lucide, TanStack Query, managed and served with **Bun** |
| Infrastructure | Docker Compose: `db`, `backend`, `frontend`, `nginx` |
| Tests | pytest + pytest-django (PostgreSQL), Vitest, Playwright E2E |

Everything financial — discounts, VAT, totals, stock, balances, aging — is
calculated by Django with `Decimal`, inside database transactions, and
re-calculated on every save. The browser never supplies authoritative numbers.

---

## Quick start (Docker)

**Easiest:** install [Docker Desktop](https://www.docker.com/products/docker-desktop), start it, then from this folder run

```bash
./start.sh          # macOS / Linux
.\start.ps1         # Windows PowerShell (if blocked: powershell -ExecutionPolicy Bypass -File .\start.ps1)
```

It creates `.env` with random secrets (and demo data), starts everything, waits
until it is ready and prints the URL, username and password.

**Windows without Docker** (e.g. "virtualization support not detected"): from this folder run

```powershell
powershell -ExecutionPolicy Bypass -File .\start-windows.ps1
```

It installs PostgreSQL 16, uv and Bun with `winget` if missing, creates the
database and a random admin password, loads demo data, builds the frontend and
serves the app at **http://localhost:4173** (Django via waitress on port 8000,
SvelteKit preview server in front). Stop it with `.\stop-windows.ps1`; later
runs of `start-windows.ps1` just start it again. Logs and the generated admin
password are in the `.local` folder. If you already had PostgreSQL installed,
the script asks for its `postgres` password once.

**Manual:**

```bash
cp .env.example .env
# edit .env: set SECRET_KEY, POSTGRES_PASSWORD, DJANGO_SUPERUSER_PASSWORD
# optional for a demo: LOAD_DEMO_DATA=True
docker compose up --build
```

Open **http://localhost** (or `http://localhost:$HTTP_PORT`) and sign in with
`DJANGO_SUPERUSER_USERNAME` / `DJANGO_SUPERUSER_PASSWORD`.

With `LOAD_DEMO_DATA=True` the database is filled (only if empty) with clearly
marked **demo data** and demo users `demo_admin`, `demo_manager`,
`demo_accountant`, `demo_staff` (password `Demo@12345`). Never enable demo
data in production.

> If you serve on a port other than 80, add it to `CSRF_TRUSTED_ORIGINS`
> (e.g. `http://localhost:8080`) and `FRONTEND_URL`.

---

## Features

**Transactions** — sales invoices (`INV-000001`), purchase bills (`BILL-000001`),
customer receipts (`RCPT-000001`), vendor payments (`PAY-000001`); gap-free,
concurrency-safe numbering. Item-level **and** invoice-level discounts
(percentage or fixed), per-product tax rates, due dates from credit terms,
pay-on-save (paid / partial), Save & Print, Save & PDF, cancellation with
reversing entries (financial records are never deleted).

**Expenses** — day-to-day costs (`EXP-000001`) by category (rent, salaries,
utilities, … — categories are editable), optional vendor or free-text payee,
optional VAT calculated by the server, cancellation instead of deletion,
printable expense voucher, and an expense report with a per-category
breakdown. Expenses also appear on the dashboard and in global search.

**Inventory** — products (unique SKU, soft-deactivation), immutable stock ledger
(ORM guard + PostgreSQL trigger + `CHECK running_balance >= 0`), row-locked
stock validation that blocks overselling (including back-dated sales that
would make history negative), current / as-of / between-dates stock, stock
register with cost & retail valuation, audited stock adjustments.

**Parties** — customers and vendors with PAN/VAT, credit terms (Cash, 7–90 days,
custom), credit limit, quick-add from entry screens, profile dashboards.

**Receivables / Payables** — allocation of one payment across many invoices,
FIFO auto-allocation, advances / unallocated payments, un-allocation,
automatic PAID / PARTIAL / UNPAID status, customer & vendor ledgers with running
balance, statements, and **historical as-of aging** (Current, 1–30, 31–60,
61–90, 91–120, 120+) that ages only the outstanding part of each document.

**Reports** — sales, purchases, expenses, stock, stock ledger, receivables, payables,
receivables aging, payables aging, customer/vendor ledgers and statements;
every one exports **CSV / PDF / Print** using the on-screen filters.

**Dashboard** — today's and period sales, purchases, receipts, payments,
receivables/payables and overdue, stock value, low stock, customers, vendors,
trends, receipts vs payments, top products, stock value by product.

**Administration** — users, roles (ADMIN, MANAGER, ACCOUNTANT, STAFF) enforced
by the API, append-only audit log (user, action, model, object, timestamp, IP,
before/after data), business settings, global debounced search (Ctrl+K).

---

## Local development (without Docker)

Requirements: Python 3.11+, [uv](https://docs.astral.sh/uv/), [Bun](https://bun.sh) ≥ 1.3, PostgreSQL 16.

```bash
# Database (example)
createuser -P accounting            # password: accounting
createdb -O accounting accounting
psql -c "ALTER USER accounting CREATEDB"   # lets pytest create its test DB

# Backend
cd backend
cp ../.env.example .env              # then set DEBUG=True and
                                     # DATABASE_URL=postgresql://accounting:accounting@localhost:5432/accounting
uv sync
uv run python manage.py migrate
uv run python manage.py createsuperuser
uv run python manage.py seed_demo_data     # optional DEMO data
uv run python manage.py runserver          # http://127.0.0.1:8000

# Frontend (second terminal)
cd frontend
bun install
bun run dev                          # http://localhost:5173 (proxies /api to :8000)
```

## Testing

```bash
cd backend && uv run pytest          # 154 tests against PostgreSQL
cd frontend && bun run test          # unit tests (Vitest)
cd frontend && bun run check         # svelte-check / TypeScript
cd frontend && bun run build         # production build

# End-to-end (against a running stack, e.g. docker compose on port 80)
cd frontend
bunx playwright install chromium     # once
E2E_BASE_URL=http://localhost bun run test:e2e
```

The E2E suite performs the full workflow: login → dashboard → create product,
customer, vendor → purchase with discounts → stock increase → sale with item
and invoice discounts → VAT → stock decrease → receipt + allocation → customer
balance, ledger, statement → receivables aging → vendor payment → vendor ledger
→ payables aging → blocked overselling → stock ledger → sales & purchase
reports → CSV → PDF → invoice print. It uses unique names so it can be re-run.

## Documentation

* [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — architecture, schema, calculation
  order, inventory, ledgers, aging, security, design decisions
* [docs/API.md](docs/API.md) — REST endpoints and response format
* [docs/OPERATIONS.md](docs/OPERATIONS.md) — production deployment, migrations,
  backup & restore, recovery
* [docs/DELIVERY_REPORT.md](docs/DELIVERY_REPORT.md) — final delivery report,
  test results and known limitations

## Repository layout

```
backend/            Django project (uv): config/, apps/<module>/, tests/
frontend/           SvelteKit app (Bun): src/routes, src/lib/{api,components,stores,types,utilities}, e2e/
nginx/nginx.conf    reverse proxy (single origin for SPA + API)
scripts/            backup.sh / restore.sh
Dockerfile          multi-target image (backend, frontend)
docker-compose.yml  db, backend, frontend, nginx
.env.example        all configuration (no secrets committed)
```
