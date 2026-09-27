"""Inventory engine.

All stock changes go through :func:`post_movement`, which must be called
inside a transaction with the product row locked (see :func:`lock_products`).
Locking products in ascending id order guarantees concurrent transactions
touching several products never deadlock, and serialises stock checks so two
sales can never both consume the same units (no overselling).
"""

from __future__ import annotations

import datetime as dt
from collections.abc import Iterable
from decimal import Decimal

from django.db import transaction
from django.db.models import Sum
from django.db.models.functions import Coalesce
from django.utils import timezone

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot
from apps.common.exceptions import BusinessError, InsufficientStockError
from apps.common.models import DocumentSequence
from apps.common.money import ZERO, qty
from apps.products.models import Product

from .models import MovementType, StockAdjustment, StockMovement

QTY_ZERO = Decimal("0.000")


def _require_atomic():
    if not transaction.get_connection().in_atomic_block:
        raise RuntimeError("Inventory operations must run inside transaction.atomic().")


def lock_products(product_ids: Iterable[int]) -> dict[int, Product]:
    """SELECT ... FOR UPDATE the given products in a deterministic order."""
    _require_atomic()
    ids = sorted(set(product_ids))
    products = Product.objects.select_for_update().filter(pk__in=ids).order_by("pk")
    found = {p.pk: p for p in products}
    missing = [i for i in ids if i not in found]
    if missing:
        raise BusinessError(f"Product(s) not found: {missing}", code="PRODUCT_NOT_FOUND", status_code=404)
    return found


def calculate_stock(product: Product | int) -> Decimal:
    """Current stock = balance after the most recently posted movement."""
    product_id = product.pk if isinstance(product, Product) else product
    latest = StockMovement.objects.filter(product_id=product_id).order_by("-id").values_list("running_balance", flat=True).first()
    return latest if latest is not None else QTY_ZERO


def calculate_historical_stock(product: Product | int, as_of: dt.date) -> Decimal:
    """Closing stock at date T = opening + qty in up to T - qty out up to T."""
    product_id = product.pk if isinstance(product, Product) else product
    agg = StockMovement.objects.filter(product_id=product_id, date__lte=as_of).aggregate(
        qin=Coalesce(Sum("quantity_in"), ZERO), qout=Coalesce(Sum("quantity_out"), ZERO)
    )
    return qty(agg["qin"] - agg["qout"])


def calculate_stock_between(product: Product | int, start: dt.date, end: dt.date) -> dict:
    product_id = product.pk if isinstance(product, Product) else product
    opening = calculate_historical_stock(product_id, start - dt.timedelta(days=1))
    agg = StockMovement.objects.filter(product_id=product_id, date__gte=start, date__lte=end).aggregate(
        qin=Coalesce(Sum("quantity_in"), ZERO), qout=Coalesce(Sum("quantity_out"), ZERO)
    )
    return {
        "opening": opening,
        "quantity_in": qty(agg["qin"]),
        "quantity_out": qty(agg["qout"]),
        "closing": qty(opening + agg["qin"] - agg["qout"]),
    }


def post_movement(
    product: Product,
    *,
    date: dt.date,
    transaction_type: str,
    quantity_in: Decimal = ZERO,
    quantity_out: Decimal = ZERO,
    reference_type: str = "",
    reference_id: int | None = None,
    reference_number: str = "",
    unit_cost: Decimal = ZERO,
    notes: str = "",
    user=None,
) -> StockMovement:
    """Append one ledger row. Caller must hold the product's row lock."""
    _require_atomic()
    quantity_in, quantity_out = qty(quantity_in), qty(quantity_out)
    if (quantity_in > 0) == (quantity_out > 0):
        raise BusinessError("A stock movement must be either inward or outward.", code="INVALID_MOVEMENT")
    current = calculate_stock(product)
    new_balance = qty(current + quantity_in - quantity_out)
    if new_balance < 0:
        raise InsufficientStockError(
            f"Insufficient stock for {product.name}. Available: {current.normalize():f} {product.get_unit_display()}, "
            f"requested: {quantity_out.normalize():f} {product.get_unit_display()}.",
            details={
                "product_id": product.pk,
                "product": product.name,
                "sku_code": product.sku_code,
                "available": str(current),
                "requested": str(quantity_out),
                "unit": product.get_unit_display(),
            },
        )
    return StockMovement.objects.create(
        product=product,
        date=date,
        transaction_type=transaction_type,
        reference_type=reference_type,
        reference_id=reference_id,
        reference_number=reference_number,
        quantity_in=quantity_in,
        quantity_out=quantity_out,
        running_balance=new_balance,
        unit_cost=unit_cost,
        notes=notes[:255],
        created_by=user if getattr(user, "is_authenticated", False) else None,
    )


def post_opening_stock(product: Product, *, user=None, date: dt.date | None = None) -> StockMovement | None:
    if product.opening_stock is None or product.opening_stock <= 0:
        return None
    lock_products([product.pk])
    return post_movement(
        product,
        date=date or timezone.localdate(),
        transaction_type=MovementType.OPENING_STOCK,
        quantity_in=product.opening_stock,
        reference_type="product",
        reference_id=product.pk,
        reference_number=product.sku_code,
        unit_cost=product.purchase_price,
        notes="Opening stock",
        user=user,
    )


def check_availability(requirements: dict[int, Decimal], products: dict[int, Product]) -> None:
    """Validate that each product has enough stock for the requested quantity.

    ``requirements`` maps product id -> total quantity requested across all
    lines, so two lines of the same product are checked together.
    """
    for product_id, requested in requirements.items():
        product = products[product_id]
        available = calculate_stock(product)
        if requested > available:
            raise InsufficientStockError(
                f"Insufficient stock for {product.name}. Available: {available.normalize():f} "
                f"{product.get_unit_display()}, requested: {requested.normalize():f} {product.get_unit_display()}.",
                details={
                    "product_id": product.pk,
                    "product": product.name,
                    "sku_code": product.sku_code,
                    "available": str(available),
                    "requested": str(requested),
                    "unit": product.get_unit_display(),
                },
            )


@transaction.atomic
def create_stock_adjustment(*, product_id: int, quantity: Decimal, reason: str, date: dt.date, notes: str = "", user) -> StockAdjustment:
    quantity = qty(quantity)
    if quantity == 0:
        raise BusinessError("Adjustment quantity cannot be zero.", code="INVALID_QUANTITY")
    products = lock_products([product_id])
    product = products[product_id]
    before = calculate_stock(product)
    number = DocumentSequence.next_number(DocumentSequence.ADJUSTMENT)
    adjustment = StockAdjustment(
        adjustment_number=number,
        product=product,
        date=date,
        quantity=quantity,
        reason=reason,
        notes=notes,
        stock_before=before,
        stock_after=qty(before + quantity),
        created_by=user,
    )
    if adjustment.stock_after < 0:
        raise InsufficientStockError(
            f"Adjustment would make stock negative for {product.name}. Available: {before.normalize():f}.",
            details={"product_id": product.pk, "available": str(before), "requested": str(-quantity)},
        )
    adjustment.save()
    post_movement(
        product,
        date=date,
        transaction_type=MovementType.ADJUSTMENT,
        quantity_in=quantity if quantity > 0 else ZERO,
        quantity_out=-quantity if quantity < 0 else ZERO,
        reference_type="adjustment",
        reference_id=adjustment.pk,
        reference_number=number,
        unit_cost=product.purchase_price,
        notes=f"{adjustment.get_reason_display()}{': ' + notes if notes else ''}",
        user=user,
    )
    record(AuditAction.CREATE, adjustment, after=snapshot(adjustment), user=user)
    return adjustment
