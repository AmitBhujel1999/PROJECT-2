"""Read-side queries for stock (register, ledger, valuation)."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

from django.db.models import (
    Case,
    DecimalField,
    ExpressionWrapper,
    F,
    OuterRef,
    Q,
    QuerySet,
    Subquery,
    Sum,
    Value,
    When,
)
from django.db.models.functions import Coalesce

from apps.products.models import Product

from .models import MovementType, StockMovement

QTY = DecimalField(max_digits=14, decimal_places=3)
MONEY = DecimalField(max_digits=20, decimal_places=2)
ZERO_QTY = Value(Decimal("0"), output_field=QTY)


class StockStatus:
    IN_STOCK = "IN_STOCK"
    LOW_STOCK = "LOW_STOCK"
    OUT_OF_STOCK = "OUT_OF_STOCK"


def annotate_current_stock(qs: QuerySet[Product]) -> QuerySet[Product]:
    latest = StockMovement.objects.filter(product=OuterRef("pk")).order_by("-id").values("running_balance")[:1]
    return qs.annotate(current_stock=Coalesce(Subquery(latest, output_field=QTY), ZERO_QTY))


def stock_register(
    qs: QuerySet[Product] | None = None,
    *,
    as_of: dt.date | None = None,
    start: dt.date | None = None,
) -> QuerySet[Product]:
    """Annotate products with purchased/sold totals, stock, valuation and status.

    * ``as_of``: stock is the historical closing stock at that date.
    * ``start``: purchased/sold totals only include movements from that date.
    """
    qs = qs if qs is not None else Product.objects.all()
    upto = Q() if as_of is None else Q(stock_movements__date__lte=as_of)
    period = upto if start is None else upto & Q(stock_movements__date__gte=start)

    def total(field, types):
        return Coalesce(Sum(f"stock_movements__{field}", filter=period & Q(stock_movements__transaction_type__in=types)), ZERO_QTY)

    qs = qs.annotate(
        purchased_in=total("quantity_in", [MovementType.PURCHASE]),
        purchase_reversed=total("quantity_out", [MovementType.PURCHASE_CANCEL]),
        sold_out=total("quantity_out", [MovementType.SALE]),
        sale_reversed=total("quantity_in", [MovementType.SALE_CANCEL]),
        adjusted_in=total("quantity_in", [MovementType.ADJUSTMENT]),
        adjusted_out=total("quantity_out", [MovementType.ADJUSTMENT]),
        stock_in=Coalesce(Sum("stock_movements__quantity_in", filter=upto), ZERO_QTY),
        stock_out=Coalesce(Sum("stock_movements__quantity_out", filter=upto), ZERO_QTY),
    ).annotate(
        total_purchased=ExpressionWrapper(F("purchased_in") - F("purchase_reversed"), output_field=QTY),
        total_sold=ExpressionWrapper(F("sold_out") - F("sale_reversed"), output_field=QTY),
        total_adjusted=ExpressionWrapper(F("adjusted_in") - F("adjusted_out"), output_field=QTY),
        current_stock=ExpressionWrapper(F("stock_in") - F("stock_out"), output_field=QTY),
    ).annotate(
        cost_value=ExpressionWrapper(F("current_stock") * F("purchase_price"), output_field=MONEY),
        retail_value=ExpressionWrapper(F("current_stock") * F("selling_price"), output_field=MONEY),
        stock_status=Case(
            When(current_stock__lte=0, then=Value(StockStatus.OUT_OF_STOCK)),
            When(current_stock__lte=F("reorder_level"), then=Value(StockStatus.LOW_STOCK)),
            default=Value(StockStatus.IN_STOCK),
        ),
    )
    return qs


def low_stock_products(limit: int | None = None) -> QuerySet[Product]:
    qs = stock_register(Product.objects.filter(is_active=True)).filter(
        stock_status__in=[StockStatus.LOW_STOCK, StockStatus.OUT_OF_STOCK]
    ).order_by("current_stock", "name")
    return qs[:limit] if limit else qs


def stock_ledger(
    *,
    product_id: int | None = None,
    transaction_type: str | None = None,
    start: dt.date | None = None,
    end: dt.date | None = None,
    search: str | None = None,
) -> QuerySet[StockMovement]:
    """Movements with true historical closing quantity per row.

    ``closing_quantity`` is a correlated subquery summing every movement of the
    same product up to and including this row in (date, id) order, so it is
    correct whatever filters are applied (date range, type, search). Only the
    current page is evaluated, using the (product, date, id) index.
    """
    qs = StockMovement.objects.select_related("product", "created_by")
    if product_id:
        qs = qs.filter(product_id=product_id)
    if transaction_type:
        qs = qs.filter(transaction_type=transaction_type)
    if start:
        qs = qs.filter(date__gte=start)
    if end:
        qs = qs.filter(date__lte=end)
    if search:
        qs = qs.filter(
            Q(reference_number__icontains=search) | Q(product__name__icontains=search) | Q(product__sku_code__icontains=search)
        )
    upto_row = (
        StockMovement.objects.filter(product_id=OuterRef("product_id"))
        .filter(Q(date__lt=OuterRef("date")) | Q(date=OuterRef("date"), id__lte=OuterRef("id")))
        .order_by()
        .values("product_id")
        .annotate(total=Sum(F("quantity_in") - F("quantity_out")))
        .values("total")
    )
    return qs.annotate(closing_quantity=Coalesce(Subquery(upto_row, output_field=QTY), ZERO_QTY)).order_by("date", "id")


def ledger_row(r: StockMovement) -> dict:
    closing = r.closing_quantity
    return {
        "id": r.id,
        "date": r.date,
        "product_id": r.product_id,
        "product_name": r.product.name,
        "sku_code": r.product.sku_code,
        "unit": r.product.get_unit_display(),
        "transaction_type": r.transaction_type,
        "transaction_type_display": r.get_transaction_type_display(),
        "reference_type": r.reference_type,
        "reference_id": r.reference_id,
        "reference_number": r.reference_number,
        "opening_quantity": closing - r.quantity_in + r.quantity_out,
        "quantity_in": r.quantity_in,
        "quantity_out": r.quantity_out,
        "closing_quantity": closing,
        "running_balance": r.running_balance,
        "notes": r.notes,
        "created_at": r.created_at,
        "created_by": r.created_by.username if r.created_by else None,
    }
