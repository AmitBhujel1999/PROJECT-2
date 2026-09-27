# REST API

Base path `/api/`. Session-cookie authentication; unsafe methods need the
`X-CSRFToken` header (value of the `acct_csrftoken` cookie, obtainable from
`GET /api/auth/csrf/`).

Responses:

```json
{ "success": true, "data": {}, "message": "Sale INV-000124 created successfully." }
{ "success": false, "error": { "code": "INSUFFICIENT_STOCK", "message": "Insufficient stock for Product A. Available: 7 Pcs, requested: 10 Pcs.", "details": { "available": "7.000", "requested": "10.000" } } }
```

Paginated lists return `data = {results, count, page, page_size, total_pages, …}`;
use `?page=`, `?page_size=25|50|100`, `?search=`, `?ordering=`. Reports accept
`?export=csv|pdf` (and `&inline=1` for in-browser PDF).

Common error codes: `VALIDATION_ERROR`, `AUTHENTICATION_REQUIRED` (401),
`PERMISSION_DENIED` (403), `NOT_FOUND`, `CSRF_FAILED`, `RATE_LIMITED`,
`INSUFFICIENT_STOCK` (409), `INVALID_DISCOUNT`, `INVALID_ALLOCATION`,
`ALREADY_CANCELLED`, `PRODUCT_HAS_HISTORY` (409), `DUE_DATE_OVERRIDE_DENIED`.

## Auth & administration

| Method | Path | Notes |
|---|---|---|
| GET | `auth/csrf/` | sets CSRF cookie |
| POST | `auth/login/` | `{username, password}` |
| POST | `auth/logout/` | |
| GET/PATCH | `auth/me/` | profile (+ `permissions`) |
| POST | `auth/password/change/` | `{current_password, new_password}` |
| POST | `auth/password/reset/` | `{email}` — emails a link |
| POST | `auth/password/reset/confirm/` | `{uid, token, new_password}` |
| GET/POST/PATCH | `users/`, `users/{id}/` | ADMIN only |
| GET | `roles/` | role → permissions matrix |
| GET | `audit-logs/` | filters: action, model_name, object_id, user, start_date, end_date |
| GET/PUT | `settings/` | business settings (PUT: ADMIN) |
| GET | `health/` | liveness |

## Master data

| Method | Path | Notes |
|---|---|---|
| GET/POST | `products/` | filters: is_active, unit, search (name/SKU) |
| GET/PATCH/DELETE | `products/{id}/` | DELETE only if never used (else 409) |
| POST | `products/{id}/activate/`, `products/{id}/deactivate/` | |
| GET/POST | `parties/`, `customers/`, `vendors/` | search name/phone/PAN/email; quick-add |
| GET/PATCH | `customers/{id}/`, `vendors/{id}/` | |
| POST | `customers/{id}/activate/` · `deactivate/` (same for vendors) | |
| GET | `customers/{id}/summary/`, `vendors/{id}/summary/` | profile totals |

## Transactions

| Method | Path | Notes |
|---|---|---|
| GET/POST | `sales/` | filters: start_date, end_date, customer, invoice_number, payment_status, status |
| GET | `sales/{id}/` | with items and allocations |
| POST | `sales/calculate/` | server-side preview (nothing saved) |
| POST | `sales/{id}/cancel/` | `{reason}` — reverses stock, releases allocations |
| GET | `sales/{id}/pdf/` | printable tax invoice |
| … | `purchases/…` | same shape (`vendor_bill_number` extra field) |
| GET/POST | `customer-receipts/`, `vendor-payments/` | create with `allocations: [{document, amount}]` or `auto_allocate: true` |
| POST | `…/{id}/allocate/`, `…/{id}/auto-allocate/`, `…/{id}/unallocate/`, `…/{id}/cancel/` | |
| GET | `…/{id}/pdf/`, `…/open-documents/?party=` | |

Sale/purchase create body:

```json
{
  "party": 12, "date": "2026-09-27", "due_date": "2026-10-27",
  "discount_type": "PERCENTAGE", "discount_value": "5",
  "items": [{ "product": 3, "quantity": "4", "unit_price": "2000", "discount_type": "FIXED", "discount_value": "400" }],
  "payment": { "pay_in_full": true, "payment_method": "CASH" },
  "notes": ""
}
```

## Inventory

| Method | Path | Notes |
|---|---|---|
| GET | `inventory/` | stock register: search, status, as_of, start_date, ordering |
| GET | `inventory/{product_id}/` | current, `?as_of=`, `?start_date=&end_date=` |
| GET | `inventory/ledger/` | product, transaction_type, start_date, end_date |
| GET/POST | `inventory/adjustments/` | `{product, date, quantity (signed), reason, notes}` |

## Ledgers, statements, aging

| Path | Notes |
|---|---|
| `customers/{id}/ledger/`, `vendors/{id}/ledger/` | start_date, end_date; paginated; export |
| `customers/{id}/statement/`, `vendors/{id}/statement/` | period (defaults to month to date); export |
| `customers/{id}/aging/`, `vendors/{id}/aging/` | `?as_of=` document-level aging + advances |

## Reports

| Path | Notes |
|---|---|
| `reports/sales/`, `reports/purchases/` | start_date, end_date, party, number, payment_status, status (ACTIVE/CANCELLED/ALL), search |
| `reports/stock/`, `reports/stock-ledger/` | same filters as inventory endpoints |
| `reports/receivables/`, `reports/payables/` | open documents as_of, party, overdue |
| `reports/receivables-aging/`, `reports/payables-aging/` | as_of, party, search |
| `dashboard/` | start_date, end_date |
| `search/?q=` | grouped results; `&type=products|customers|vendors|invoices|bills|receipts|payments` paginates one type |
