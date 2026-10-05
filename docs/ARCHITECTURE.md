# Architecture

## 1. Overview

```
Browser ──► nginx :80 ──┬── /api/*, /admin/*, /static/*  ──► Django + DRF (gunicorn :8000) ──► PostgreSQL
                        └── everything else              ──► SvelteKit (Bun, adapter-node :3000)
```

* **One origin.** nginx serves the SPA and the API from the same host, so the
  HTTP-only session cookie and CSRF cookie are first-party and no CORS is
  needed in production (CORS is only enabled for `/api/` origins listed in
  `CORS_ALLOWED_ORIGINS`, for development).
* **Django is the single source of truth.** The SvelteKit app is a client-side
  SPA (`ssr = false`); it renders data it fetches from the API with the user's
  session. It holds no business logic that is trusted.

## 2. Backend

```
backend/
  config/            settings (env driven), urls, wsgi
  apps/
    common/          money helpers, calculation engine, document & payment base
                     models, generic receivable/payable engine, numbering,
                     business settings, pagination, envelope renderer, errors,
                     management commands (seed_demo_data, ensure_admin)
    users/           custom User (role), auth API, role permission matrix
    audit/           append-only AuditLog, request context middleware
    products/        Product + services
    parties/         Party (CUSTOMER/VENDOR) + services
    inventory/       StockMovement (ledger), StockAdjustment, services, selectors
    purchases/       Purchase, PurchaseItem, create/cancel services
    sales/           Sale, SaleItem, create/cancel services
    receivables/     CustomerReceipt, CustomerReceiptAllocation, RECEIVABLE config
    payables/        VendorPayment, VendorPaymentAllocation, PAYABLE config
    expenses/        ExpenseCategory, Expense (EXP numbering, cancel-only) + services
    reports/         reports, dashboard, global search, CSV/PDF exporters, document PDFs
  tests/             pytest suite (runs on PostgreSQL)
```

**Service layer.** All state-changing financial logic lives in `services.py`
modules (and the shared engines in `apps/common`). Views only validate input
shape, call a service, and serialise the result. Main services:

| Service | Location |
|---|---|
| `create_sale`, `cancel_sale`, `calculate_sale_totals` | `apps/sales/services.py` |
| `create_purchase`, `cancel_purchase`, `calculate_purchase_totals` | `apps/purchases/services.py` |
| `calculate_document` (discount + tax engine) | `apps/common/calculations.py` |
| `calculate_stock`, `calculate_historical_stock`, `calculate_stock_between`, `available_on`, `post_movement`, `create_stock_adjustment` | `apps/inventory/services.py` |
| `create_customer_receipt`, `allocate_customer_payment`, `calculate_customer_ledger`, `calculate_customer_aging` | `apps/receivables/services.py` |
| `create_vendor_payment`, `allocate_vendor_payment`, `calculate_vendor_ledger`, `calculate_vendor_aging` | `apps/payables/services.py` |
| generic engine behind both of the above | `apps/common/party_accounts.py` |

Receivables and payables are mirror images; they share one engine
parameterised by an `AccountConfig` (document model, payment model, allocation
model, debit/credit orientation), so the logic exists exactly once.

**API conventions.** Every JSON response is an envelope:
`{"success": true, "data": …, "message": "…"}` or
`{"success": false, "error": {"code": "INSUFFICIENT_STOCK", "message": "…", "details": {…}}}`.
Decimals are always serialised as strings. Lists are paginated server-side
(`page`, `page_size` ∈ {25, 50, 100}).

## 3. Database schema (main tables)

| Table | Key columns / constraints |
|---|---|
| `users_user` | Django user + `role` (ADMIN/MANAGER/ACCOUNTANT/STAFF) |
| `products_product` | `sku_code` UNIQUE + index, name index, `unit`, `opening_stock`, `reorder_level`, `purchase_price`, `selling_price`, `tax_rate`, `is_active`; CHECKs: prices ≥ 0, stock ≥ 0, 0 ≤ tax ≤ 100 |
| `parties_party` | `type`, name, `pan_vat_no`, phone, email, address, `credit_terms`, `credit_days`, `credit_limit ≥ 0`, `is_active` — **no balance column** |
| `purchases_purchase` / `sales_sale` | `bill_number` / `invoice_number` UNIQUE, date, due_date (CHECK ≥ date), party FK (PROTECT), subtotal, item_discount_total, discount_type/value/amount, taxable, tax, total, `amount_paid` (CHECK 0 ≤ paid ≤ total), payment_status, status (ACTIVE/CANCELLED), cancel metadata, created_by |
| `purchases_purchaseitem` / `sales_saleitem` | product FK (PROTECT), quantity (CHECK > 0), unit cost/price (≥ 0), gross, discount type/value/amount, invoice_discount_share, taxable, tax_rate, tax, line total; all CHECK ≥ 0 |
| `inventory_stockmovement` | product, date, transaction_type, reference type/id/number, quantity_in/out (exactly one > 0), `running_balance` (CHECK ≥ 0), unit_cost, created_by; UPDATE/DELETE blocked by trigger |
| `inventory_stockadjustment` | `adjustment_number` UNIQUE, product, date, signed quantity (≠ 0), reason, notes, stock before/after, created_by |
| `receivables_customerreceipt` / `payables_vendorpayment` | `receipt_number` / `payment_number` UNIQUE, party, date, amount (> 0), `allocated_amount` (CHECK ≤ amount), method, reference, status, cancel metadata |
| `expenses_expensecategory` | name (case-insensitive UNIQUE), description, `is_active` |
| `expenses_expense` | `expense_number` UNIQUE, date, category FK (PROTECT), optional vendor FK (PROTECT) or free-text payee, description, amount (> 0, excl. tax), tax_rate (0–100), tax, total (CHECK = amount + tax), method, reference, status, cancel metadata |
| `…_allocation` | payment FK, document FK, amount (> 0), effective `date`, `is_active`, `voided_at/by/reason` |
| `common_documentsequence` | per document type `prefix`, `last_number` (row-locked counter) |
| `common_businesssettings` | singleton: name, address, PAN/VAT, currency, tax label, default tax rate |
| `audit_auditlog` | user, username, action, model, object id/repr, timestamp, IP, before/after JSON |

Migrations live in each app's `migrations/` folder. Notable hand-written
migrations: `common/0002` seeds document sequences and the settings row;
`inventory/0002` installs the immutability trigger; `expenses/0002` seeds the
`EXP` sequence and default expense categories. See OPERATIONS.md for the
migration workflow.

## 4. Discount & tax calculation

One engine (`apps/common/calculations.py`) is used for both sales and purchases:

```
Quantity × Unit Price                → Gross amount (per line, rounded)
Gross − Item discount (% or fixed)   → Net item amount
Σ Net item amounts                   → Net subtotal
Invoice discount (% or fixed)        → allocated pro-rata to lines by net amount
Net item − Invoice share             → Taxable amount (per line)
Taxable × line tax rate              → Tax (per line, rounded)
Taxable + Tax                        → Line total;  Σ lines → document totals
```

* Rounding: 2 dp, `ROUND_HALF_UP`, per line; document totals are sums of rounded
  line values, so printed lines always add up. Pro-rata shares are rounded and
  the remainder goes to the largest line so shares sum exactly.
* Why allocate the invoice discount to lines? Products carry different tax
  rates; VAT must be computed on each line's own taxable amount.
* Validation: quantity > 0; price ≥ 0; tax 0–100; discount ≥ 0; percentage
  ≤ 100; fixed discount ≤ the amount it applies to — so a negative taxable
  amount is impossible.
* The tax rate always comes from the product master. Any totals sent by the
  client are ignored. `pay_in_full` lets the server use its own total.

## 5. Inventory

* **Immutable ledger.** Every stock change appends one `StockMovement`
  (OPENING_STOCK, PURCHASE, SALE, ADJUSTMENT, PURCHASE_CANCEL, SALE_CANCEL).
  Cancellations append reversing rows; nothing is rewritten.
* **Concurrency.** Stock-affecting services lock the product rows with
  `SELECT … FOR UPDATE` in ascending id order (no deadlocks), then read and
  validate stock, then write. Concurrent sales of the same item are therefore
  serialised; the loser sees the reduced stock and gets `INSUFFICIENT_STOCK`.
  The `running_balance ≥ 0` CHECK is a final database-level guard.
* **Back-dated documents.** Outward quantity is validated against
  `available_on(date)` = min(closing stock on that date, running balance after
  every later movement), so a back-dated sale cannot consume stock that was
  received later or that later sales already used.
* **Historical stock.** `closing(T) = Σ quantity_in − Σ quantity_out` for
  movements dated ≤ T (opening stock is itself a movement). Stock ledger rows
  carry true historical opening/closing quantities computed by a correlated
  subquery over `(product, date, id)`.
* **Valuation** uses the product's current purchase price (cost) and selling
  price (retail) × stock.

## 6. Receivables, payables, ledgers and aging

* **Balances are derived, never stored.** Party balance at date T =
  documents issued ≤ T − documents cancelled ≤ T − payments ≤ T + payments
  cancelled ≤ T.
* **Allocation.** A payment can be split across many invoices/bills. The payment
  and documents are row-locked; each allocation is checked against the document's
  open balance and the payment's unallocated amount (also enforced by CHECK
  constraints). `amount_paid` / `payment_status` on documents and
  `allocated_amount` on payments are caches maintained only by the engine.
* **Advances.** Whatever is not allocated is an advance, shown in its own column
  and never aged as overdue. It can be allocated later.
* **Voiding, not deleting.** Un-allocation and cancellations mark allocations
  inactive with `voided_at`, so the past can be reconstructed.
* **Ledger.** Customer: invoice = debit, receipt = credit. Vendor: bill =
  credit, payment = debit. Cancellations appear as reversal entries on the
  cancellation date. Running balance = amount owed.
* **Aging as of T.** For each document dated ≤ T and not cancelled by T:
  outstanding = total − allocations effective ≤ T and not voided by T. Bucket by
  `T − due_date`: ≤ 0 Current, 1–30, 31–60, 61–90, 91–120, 120+. Computed in SQL
  with `SUM … FILTER` per bucket. An allocation's effective date is the later of
  the payment and document dates.

## 7. Frontend

```
frontend/src/
  routes/(auth)/        login, forgot-password, reset-password
  routes/(app)/         dashboard, sales, purchases, receipts, payments, products,
                        inventory (+ledger, adjustments), customers, vendors,
                        receivables/*, payables/*, reports/*, settings/*, profile
  lib/api/client.ts     fetch wrapper (session cookie, CSRF header, envelope, errors)
  lib/stores/           auth (display-only permissions), toast, ListState (server pagination)
  lib/components/ui/    UI kit on bits-ui (dialog, confirm, combobox, tabs, …)
  lib/components/…      documents, payments, parties, accounts, inventory, reports, layout
  lib/utilities/        formatting (decimal-string safe), debounce, chart palette
  e2e/                  Playwright tests
```

* Entry screens call `/calculate/` (debounced) to show server-computed totals;
  on save Django recalculates everything again.
* Money is formatted from decimal strings (lakh grouping); any display-only
  sums use integer cents (`BigInt`), never floats.
* Responsive shell: persistent sidebar (≥ 1024 px), icon rail (768–1023 px),
  drawer (< 768 px); wide tables scroll horizontally inside their cards.
* Accessibility: labelled fields, ARIA combobox/listbox pattern, keyboard
  navigation (Ctrl+K search, Ctrl+S save, Alt+N new row), live-region toasts.

## 8. Security

* Session authentication with HTTP-only `acct_sessionid` cookie; session key
  rotated on login; nothing sensitive in localStorage.
* CSRF: enforced on all unsafe requests and on login/password-reset; the SPA
  echoes the CSRF cookie in `X-CSRFToken`.
* Authorization: `RolePermission` checks a permission matrix on every endpoint
  (`apps/users/permissions.py`). The frontend only hides controls.
* HTTPS-ready: `USE_HTTPS=True` enables secure cookies, HSTS (1 year, preload),
  SSL redirect and trusts `X-Forwarded-Proto`; `manage.py check --deploy` is clean.
* Headers: CSP (`default-src 'none'` for the API; nonce-based strict CSP for the
  SPA from SvelteKit), `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy`,
  `Permissions-Policy`, `Cache-Control: no-store` on API responses.
* Rate limiting: DRF throttles (anon, user, login, password reset) and nginx
  `limit_req` on login and API.
* Input validation in serializers + database CHECK / UNIQUE / FK constraints.
* CSV exports neutralise spreadsheet formula injection.
* Audit log for sales, purchases, receipts, payments, allocations, adjustments,
  products, parties, users, role changes, settings, logins and password events;
  audit rows cannot be modified or deleted through the application.
* Secrets only from environment variables; `.env` is git-ignored.
* The backend container runs as a non-root user and is not published to the host.

## 9. Key design decisions

1. **PostgreSQL-only** (settings refuse other engines) — needed for row locks,
   CHECK constraints, triggers and window/filtered aggregates.
2. **SPA + single origin** instead of SSR — keeps authoritative logic in Django
   and cookie auth simple; nginx gives one origin.
3. **Derived balances, cached statuses** — balances are always computed; only
   `amount_paid`/`allocated_amount` are cached for speed and are rebuilt from
   allocations whenever they change.
4. **Cancel, don't delete** — documents have `ACTIVE`/`CANCELLED`; stock and
   money effects are reversed with new records.
5. **Shared engines** for sales/purchases and receivables/payables to avoid
   divergent copies of critical logic.
6. **bits-ui directly** — the shadcn-svelte CLI requires an interactive preset
   prompt, so the equivalent shadcn-style components were written on bits-ui.
