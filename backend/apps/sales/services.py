"""Sales engine: create_sale(), calculate_sale_totals(), cancel_sale().

Sale -> validate stock (under row locks) -> Sale Items -> Stock Ledger OUT,
all in one database transaction; any failure rolls everything back.

Concurrency: products are locked with SELECT ... FOR UPDATE (ascending id)
before stock is read, so concurrent sales of the same product are serialised
and the second one sees the reduced stock. The CHECK constraint on
StockMovement.running_balance is a final database-level guard.
"""

from __future__ import annotations

from collections import defaultdict
from decimal import Decimal

from django.db import transaction
from django.utils import timezone

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot
from apps.common.calculations import DocumentTotals
from apps.common.documents import apply_totals, build_totals, resolve_due_date
from apps.common.exceptions import BusinessError
from apps.common.models import DocumentSequence, DocumentStatus
from apps.inventory.models import MovementType
from apps.inventory.services import available_on, calculate_stock, check_availability, lock_products, post_movement
from apps.parties.models import Party, PartyType
from apps.products.models import Product

from .models import Sale, SaleItem


def _customer(party_id: int) -> Party:
    customer = Party.objects.filter(pk=party_id, type=PartyType.CUSTOMER).first()
    if customer is None:
        raise BusinessError("Customer not found.", code="CUSTOMER_NOT_FOUND")
    if not customer.is_active:
        raise BusinessError(f"Customer {customer.name} is inactive.", code="PARTY_INACTIVE")
    return customer


def _products(items: list[dict], *, lock: bool) -> dict[int, Product]:
    ids = [i["product"] for i in items]
    if lock:
        products = lock_products(ids)
    else:
        products = {p.pk: p for p in Product.objects.filter(pk__in=ids)}
        missing = set(ids) - set(products)
        if missing:
            raise BusinessError(f"Product(s) not found: {sorted(missing)}", code="PRODUCT_NOT_FOUND")
    inactive = [p.name for p in products.values() if not p.is_active]
    if inactive:
        raise BusinessError(f"Inactive product(s) cannot be sold: {', '.join(inactive)}", code="PRODUCT_INACTIVE")
    return products


def _requirements(items: list[dict]) -> dict[int, Decimal]:
    required: dict[int, Decimal] = defaultdict(lambda: Decimal("0"))
    for item in items:
        required[item["product"]] += item["quantity"]
    return dict(required)


def calculate_sale_totals(data: dict) -> tuple[DocumentTotals, dict[int, Decimal]]:
    products = _products(data["items"], lock=False)
    totals = build_totals(
        data["items"], products, price_attr="selling_price",
        discount_type=data.get("discount_type"), discount_value=data.get("discount_value"),
    )
    stock = {pid: min(calculate_stock(pid), available_on(pid, data["date"])) for pid in products}
    return totals, stock


@transaction.atomic
def create_sale(*, data: dict, user) -> tuple[Sale, list[str]]:
    """Create a sale. Returns (sale, warnings) — warnings list low-stock products."""
    customer = _customer(data["party"])
    # 1-2. Lock the product rows, then read current stock under the lock.
    products = _products(data["items"], lock=True)
    # 3-4. Validate requested quantities (aggregated per product); reject shortfalls.
    check_availability(_requirements(data["items"]), products, data["date"])

    totals = build_totals(
        data["items"], products, price_attr="selling_price",
        discount_type=data.get("discount_type"), discount_value=data.get("discount_value"),
    )
    due_date = resolve_due_date(
        date=data["date"], requested=data.get("due_date"), credit_days=customer.credit_days,
        user=user, override_perm="documents.override_due_date",
    )
    # 5. Save the sale.
    sale = Sale(
        invoice_number=DocumentSequence.next_number(DocumentSequence.SALE),
        customer=customer,
        date=data["date"],
        due_date=due_date,
        notes=data.get("notes", ""),
        created_by=user,
    )
    apply_totals(sale, totals)
    sale.save()
    SaleItem.objects.bulk_create(
        [
            SaleItem(
                sale=sale,
                product=line.ref,
                quantity=line.quantity,
                unit_price=line.unit_price,
                gross_amount=line.gross_amount,
                discount_type=line.discount_type,
                discount_value=line.discount_value,
                discount_amount=line.discount_amount,
                invoice_discount_share=line.invoice_discount_share,
                taxable_amount=line.taxable_amount,
                tax_rate=line.tax_rate,
                tax_amount=line.tax_amount,
                total_price=line.total,
            )
            for line in totals.lines
        ]
    )
    # 6. Stock ledger OUT (post_movement re-checks the balance under the lock).
    for line in totals.lines:
        post_movement(
            line.ref,
            date=sale.date,
            transaction_type=MovementType.SALE,
            quantity_out=line.quantity,
            reference_type="sale",
            reference_id=sale.pk,
            reference_number=sale.invoice_number,
            unit_cost=line.ref.purchase_price,
            notes=f"Sale to {customer.name}",
            user=user,
        )

    payment = data.get("payment")
    if payment:
        from apps.receivables.services import receive_on_sale_creation

        receive_on_sale_creation(sale, payment=payment, user=user)
        sale.refresh_from_db()

    record(AuditAction.CREATE, sale, after=sale_snapshot(sale), user=user)

    warnings = []
    for product in products.values():
        stock = calculate_stock(product)
        if stock <= product.reorder_level:
            warnings.append(
                f"{product.name} is below reorder level ({stock.normalize():f} {product.get_unit_display()} left)."
                if stock > 0
                else f"{product.name} is now out of stock."
            )
    # 7. Commit happens when the atomic block exits.
    return sale, warnings


@transaction.atomic
def cancel_sale(sale: Sale, *, reason: str, user) -> Sale:
    sale = Sale.objects.select_for_update().get(pk=sale.pk)
    if sale.status == DocumentStatus.CANCELLED:
        raise BusinessError(f"{sale.invoice_number} is already cancelled.", code="ALREADY_CANCELLED")
    if not reason.strip():
        raise BusinessError("A cancellation reason is required.", code="REASON_REQUIRED")
    before = sale_snapshot(sale)
    items = list(sale.items.select_related("product"))
    lock_products([i.product_id for i in items])
    today = timezone.localdate()
    for item in items:
        post_movement(
            item.product,
            date=today,
            transaction_type=MovementType.SALE_CANCEL,
            quantity_in=item.quantity,
            reference_type="sale",
            reference_id=sale.pk,
            reference_number=sale.invoice_number,
            unit_cost=item.product.purchase_price,
            notes=f"Cancellation of {sale.invoice_number}",
            user=user,
        )
    try:
        from apps.receivables.services import release_sale_allocations
    except ImportError:  # pragma: no cover
        release_sale_allocations = None
    if release_sale_allocations:
        release_sale_allocations(sale, user=user)

    sale.status = DocumentStatus.CANCELLED
    sale.cancelled_at = timezone.now()
    sale.cancelled_by = user
    sale.cancel_reason = reason.strip()
    sale.save()
    record(AuditAction.CANCEL, sale, before=before, after=sale_snapshot(sale), user=user)
    return sale


def sale_snapshot(sale: Sale) -> dict:
    data = snapshot(sale)
    data["items"] = [snapshot(i) for i in sale.items.all()]
    return data
