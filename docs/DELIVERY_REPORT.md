# Final delivery report

## 1. Implemented features

All modules from the specification: Dashboard; Sales, Purchases, Customer
Receipts, Vendor Payments; Products, Stock Register, Stock Ledger, Stock
Adjustments; Customers, Vendors; Customer/Vendor Ledgers, Receivables/Payables
Aging, Customer/Vendor Statements; Sales, Purchase, Stock, Receivables and
Payables reports; Users, Roles, Audit Log, Settings; login, logout, profile,
password change and password reset; global search; CSV/PDF/print exports;
printable invoices, bills, receipts and payment vouchers; demo seed data;
Docker deployment; backup/restore scripts.

## 2. Backend architecture

Django 5.2 + DRF, one app per domain (`users, audit, products, parties,
inventory, purchases, sales, receivables, payables, reports, common`). Critical
logic lives in service modules and two shared engines (discount/tax
calculation; receivable/payable accounting). Uniform JSON envelope, server-side
pagination, env-driven settings, PostgreSQL only. See ARCHITECTURE.md §2.

## 3. Frontend architecture

SvelteKit (Svelte 5 runes, TypeScript) SPA built and served with Bun
(adapter-node), Tailwind v4 tokens, shadcn-style components on bits-ui, Lucide
icons, TanStack Query for dashboard/detail queries, a `ListState` helper for
server-paginated tables, Chart.js charts. See ARCHITECTURE.md §7.

## 4. Database schema

13 domain tables plus Django auth/session tables; all money in
`NUMERIC(16,2)`, quantities `NUMERIC(14,3)`; UNIQUE document numbers and SKU,
FK `PROTECT` on financial references, CHECK constraints (quantity > 0, prices,
discounts and taxes ≥ 0, paid ≤ total, allocated ≤ amount, due ≥ date,
running stock ≥ 0), indexes on dates/parties/status, an immutability trigger on
the stock ledger. See ARCHITECTURE.md §3.

## 5. API endpoints

All endpoints listed in the specification plus previews (`/calculate/`),
cancellation, allocation actions, PDFs, party summaries, audit, roles, users,
settings, stock-ledger and outstanding reports, and search. See API.md.

## 6. Authentication / authorization

Server-managed sessions (HTTP-only cookie), CSRF on every unsafe request and
on login, session rotation, throttled login/reset, no enumeration on reset.
Roles ADMIN / MANAGER / ACCOUNTANT / STAFF mapped to 28 permissions enforced by
`RolePermission` on every view; the UI only mirrors them.

## 7. Inventory implementation

Immutable, append-only stock ledger; products locked `FOR UPDATE` in id order
before stock is read; aggregated per-product validation; date-aware
availability so back-dated sales cannot make history negative; cancellations
post reversing movements; historical, as-of and between-dates stock;
valuation at cost and retail.

## 8. Discount implementation

Item-level and invoice-level discounts, percentage or fixed, for both sales
and purchases. Order: gross → item discount → net → invoice discount
(pro-rata per line) → taxable → per-line tax → total. Validation prevents
negative taxable amounts; the server ignores client totals and takes tax rates
from the product master.

## 9. Customer / vendor ledger implementation

Ledgers are generated from invoices/bills, receipts/payments and their
cancellations (reversal entries on the cancellation date), with opening
balance, debit/credit per the specified conventions, running balance, due date
and payment status. Statements add period headers and exports. No balance is
stored on parties.

## 10. Receivables / payables aging implementation

As-of-date aging reconstructs outstanding amounts from allocations effective
and not voided at that date, buckets by days past due date (Current, 1–30,
31–60, 61–90, 91–120, 120+), ages only the unpaid part of partially paid
documents, and reports advances separately (never as overdue). Aggregation is
done in SQL.

## 11. Testing performed

| Suite | Result |
|---|---|
| Backend pytest on PostgreSQL (`uv run pytest`) | **134 passed** |
| Frontend unit tests (`bun run test`, Vitest) | **6 passed** |
| Type/Svelte checks (`bun run check`) | **0 errors, 0 warnings** |
| Production build (`bun run build`) | OK |
| `makemigrations --check` | No changes detected |
| Visual/responsive audit: 40 routes × 390 / 820 / 1440 px on the production build | no page-level horizontal overflow, no server errors |

Backend coverage includes: calculation engine (spec examples, rounding,
validation), products (create, edit, duplicate SKU, deactivation, delete
protection), parties, purchases (discounts, VAT, stock increase, payment
status, cancellation), sales (discounts, VAT, stock decrease, insufficient
stock with rollback, **concurrent-sale protection with 4 parallel threads**,
**concurrent unique numbering with 8 threads**, back-dated oversell),
receipts/payments (partial, full, multi-document allocation, advances, later
allocation, over-allocation, cancellation), ledgers (running, opening,
historical), every aging bucket, partial-payment aging, historical aging,
reports (filters, search, pagination, CSV/PDF exports, CSV injection),
authentication (CSRF, HTTP-only cookie, password change/reset), the role
matrix, audit logging/immutability, security headers and
`manage.py check --deploy`.

## 12. E2E test results

Playwright against the Docker stack (nginx → SvelteKit → Django → PostgreSQL),
after `docker compose down -v && docker compose up --build`, run twice
back-to-back: **6 passed / 6** both times.

Workflow test (verified values): product created → customer and vendor
created → purchase 10 × 1,000 with 10 % item and 500 invoice discount →
taxable 8,500.00, VAT 1,105.00, total **9,605.00** → stock 10 → sale 4 × 2,000
with 400 item and 5 % invoice discount → taxable 7,220.00, VAT **938.60**,
total **8,158.60** → stock 6 → receipt 5,000 allocated → invoice PARTIAL,
balance **3,158.60** → customer summary, ledger and statement closing
3,158.60 → receivables aging Current 3,158.60 → vendor payment 9,605
auto-allocated → vendor ledger 0.00 → payables aging clear → sale of 100
blocked ("Available: 6 Pcs, requested: 100 Pcs"), stock still 6 → stock
ledger rows → sales and purchase reports → CSV download (filter respected) →
PDF download (`%PDF`) → invoice PDF and print (inline PDF 200).
Security tests: login redirect, 401 and CSRF rejection, HTTP-only session
cookie and clean localStorage, staff denied finance API (403), mobile drawer.

Bugs found and fixed during testing: back-dated sales could drive historical
stock negative (engine fix + tests); nginx 502 on deep routes (header buffer);
entry/summary layout overflow and clipped item picker (grid/min-width and
fixed-position listbox); frontend container missing bundled dependencies;
report summary lacked a total-including-tax figure.

## 13. Security checks

`manage.py check --deploy` → no issues with `USE_HTTPS=True` (the 4 warnings
without it are the expected secure-cookie/HSTS/SSL-redirect ones for plain
HTTP local use). Verified headers: API CSP `default-src 'none'`, SPA nonce CSP,
`X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy`, `Permissions-Policy`,
`Cache-Control: no-store`. Login rate limiting observed in practice (nginx +
DRF). No `{@html}` in the frontend; no secrets in the repository or bundle.

## 14. Docker instructions

`cp .env.example .env`, set secrets, `docker compose up --build`, open
`http://localhost`. See README.

## 15. Development instructions

`uv sync`, `uv run python manage.py migrate`, `uv run python manage.py runserver`;
`bun install`, `bun run dev`; tests with `uv run pytest`, `bun run test`,
`bun run test:e2e`. See README.

## 16. Production deployment instructions

See OPERATIONS.md (env values, TLS termination, `USE_HTTPS=True`, upgrade
procedure, verification).

## 17. Backup / restore instructions

`scripts/backup.sh` (pg_dump custom format, verified) and `scripts/restore.sh`
(pg_restore, single transaction) — tested end to end; frequency and recovery
procedure in OPERATIONS.md.

## 18. Known limitations

* **Stock valuation** uses each product's current purchase price (standard
  cost), not FIFO/weighted-average costing, and there is no COGS/general ledger
  (double-entry chart of accounts) yet — the design leaves room for a GL app.
* **Sales/purchase returns** are handled by cancelling the whole document;
  partial returns / credit notes and debit notes are not implemented yet.
* Single currency per installation (configurable code/symbol); tax is one rate
  per product line (no compound or multiple taxes per line); Nepali (BS)
  calendar dates are not provided.
* Document numbering is gap-free and global (no fiscal-year prefix reset).
* The UI is light-theme only.
* Password reset needs SMTP settings; by default emails go to the backend log.
* Image builds need network access to Docker Hub, PyPI and the npm registry
  (a CA-certificate build secret is supported for TLS-intercepting proxies).
* The E2E suite creates uniquely-named records each run; run it against a
  test/demo database, not production.
