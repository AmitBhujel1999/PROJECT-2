"""Purchase engine: create_purchase(), calculate_purchase_totals(), cancel_purchase().

Purchase -> Purchase Items -> Stock Ledger IN -> stock increases, all inside
one database transaction. Any failure rolls everything back.
"""

from __future__ import annotations

from django.db import transaction
from django.utils import timezone

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot
from apps.common.calculations import DocumentTotals
from apps.common.documents import apply_totals, build_totals, resolve_due_date
from apps.common.exceptions import BusinessError, InsufficientStockError
from apps.common.models import DocumentSequence, DocumentStatus
from apps.common.money import money
from apps.inventory.models import MovementType
from apps.inventory.services import lock_products, post_movement
from apps.parties.models import Party, PartyType
from apps.products.models import Product

from .models import Purchase, PurchaseItem


def _vendor(party_id: int, *, for_update=False) -> Party:
    qs = Party.objects.filter(pk=party_id, type=PartyType.VENDOR)
    vendor = qs.first()
    if vendor is None:
        raise BusinessError("Vendor not found.", code="VENDOR_NOT_FOUND", status_code=400)
    if not vendor.is_active:
        raise BusinessError(f"Vendor {vendor.name} is inactive.", code="PARTY_INACTIVE")
    return vendor


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
        raise BusinessError(f"Inactive product(s) cannot be used: {', '.join(inactive)}", code="PRODUCT_INACTIVE")
    return products


def calculate_purchase_totals(data: dict) -> DocumentTotals:
    products = _products(data["items"], lock=False)
    return build_totals(
        data["items"], products, price_attr="purchase_price",
        discount_type=data.get("discount_type"), discount_value=data.get("discount_value"),
    )


@transaction.atomic
def create_purchase(*, data: dict, user) -> Purchase:
    vendor = _vendor(data["party"])
    products = _products(data["items"], lock=True)
    totals = build_totals(
        data["items"], products, price_attr="purchase_price",
        discount_type=data.get("discount_type"), discount_value=data.get("discount_value"),
    )
    due_date = resolve_due_date(
        date=data["date"], requested=data.get("due_date"), credit_days=vendor.credit_days,
        user=user, override_perm="documents.override_due_date",
    )
    purchase = Purchase(
        bill_number=DocumentSequence.next_number(DocumentSequence.PURCHASE),
        vendor_bill_number=data.get("vendor_bill_number", ""),
        vendor=vendor,
        date=data["date"],
        due_date=due_date,
        notes=data.get("notes", ""),
        created_by=user,
    )
    apply_totals(purchase, totals)
    purchase.save()

    items = []
    for line in totals.lines:
        items.append(
            PurchaseItem(
                purchase=purchase,
                product=line.ref,
                quantity=line.quantity,
                unit_cost=line.unit_price,
                gross_amount=line.gross_amount,
                discount_type=line.discount_type,
                discount_value=line.discount_value,
                discount_amount=line.discount_amount,
                invoice_discount_share=line.invoice_discount_share,
                taxable_amount=line.taxable_amount,
                tax_rate=line.tax_rate,
                tax_amount=line.tax_amount,
                total_cost=line.total,
            )
        )
    PurchaseItem.objects.bulk_create(items)

    for line in totals.lines:
        post_movement(
            line.ref,
            date=purchase.date,
            transaction_type=MovementType.PURCHASE,
            quantity_in=line.quantity,
            reference_type="purchase",
            reference_id=purchase.pk,
            reference_number=purchase.bill_number,
            # Net landed cost per unit (after all discounts, excluding recoverable VAT)
            unit_cost=money(line.taxable_amount / line.quantity),
            notes=f"Purchase from {vendor.name}",
            user=user,
        )

    payment = data.get("payment")
    if payment:
        from apps.payables.services import pay_purchase_on_creation

        pay_purchase_on_creation(purchase, payment=payment, user=user)
        purchase.refresh_from_db()

    record(AuditAction.CREATE, purchase, after=purchase_snapshot(purchase), user=user)
    return purchase


@transaction.atomic
def cancel_purchase(purchase: Purchase, *, reason: str, user) -> Purchase:
    purchase = Purchase.objects.select_for_update().get(pk=purchase.pk)
    if purchase.status == DocumentStatus.CANCELLED:
        raise BusinessError(f"{purchase.bill_number} is already cancelled.", code="ALREADY_CANCELLED")
    if not reason.strip():
        raise BusinessError("A cancellation reason is required.", code="REASON_REQUIRED")
    before = purchase_snapshot(purchase)
    items = list(purchase.items.select_related("product"))
    lock_products([i.product_id for i in items])
    today = timezone.localdate()
    for item in items:
        try:
            post_movement(
                item.product,
                date=today,
                transaction_type=MovementType.PURCHASE_CANCEL,
                quantity_out=item.quantity,
                reference_type="purchase",
                reference_id=purchase.pk,
                reference_number=purchase.bill_number,
                unit_cost=item.unit_cost,
                notes=f"Cancellation of {purchase.bill_number}",
                user=user,
            )
        except InsufficientStockError as exc:
            raise InsufficientStockError(
                f"Cannot cancel {purchase.bill_number}: stock of {item.product.name} has already been used. "
                f"{exc.message}",
                details=exc.details,
            ) from exc

    try:
        from apps.payables.services import release_purchase_allocations
    except ImportError:  # pragma: no cover
        release_purchase_allocations = None
    if release_purchase_allocations:
        release_purchase_allocations(purchase, user=user)

    purchase.status = DocumentStatus.CANCELLED
    purchase.cancelled_at = timezone.now()
    purchase.cancelled_by = user
    purchase.cancel_reason = reason.strip()
    purchase.save()
    record(AuditAction.CANCEL, purchase, before=before, after=purchase_snapshot(purchase), user=user)
    return purchase


def purchase_snapshot(purchase: Purchase) -> dict:
    data = snapshot(purchase)
    data["items"] = [snapshot(i) for i in purchase.items.all()]
    return data
