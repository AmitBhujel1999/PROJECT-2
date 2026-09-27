"""Shared building blocks for trade documents (sales invoices & purchase bills).

Sales and purchases have their own concrete models/tables, but share the same
column set, validation and calculation pipeline defined here so the logic is
written exactly once.
"""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

from django.conf import settings
from django.db import models
from django.db.models import Q
from rest_framework import serializers

from .calculations import DocumentTotals, LineInput, calculate_document
from .exceptions import BusinessError
from .models import DiscountType, DocumentStatus, PaymentMethod, PaymentStatus, TimeStampedModel
from .money import MONEY_FIELD, QTY_FIELD, RATE_FIELD, ZERO, money


class TradeDocument(TimeStampedModel):
    date = models.DateField(db_index=True)
    due_date = models.DateField(db_index=True)
    subtotal = models.DecimalField(**MONEY_FIELD, default=ZERO, help_text="Sum of gross line amounts.")
    item_discount_total = models.DecimalField(**MONEY_FIELD, default=ZERO)
    discount_type = models.CharField(max_length=12, choices=DiscountType.choices, null=True, blank=True)
    discount_value = models.DecimalField(**MONEY_FIELD, default=ZERO)
    discount_amount = models.DecimalField(**MONEY_FIELD, default=ZERO, help_text="Invoice-level discount amount.")
    taxable_amount = models.DecimalField(**MONEY_FIELD, default=ZERO)
    tax_amount = models.DecimalField(**MONEY_FIELD, default=ZERO)
    total_amount = models.DecimalField(**MONEY_FIELD, default=ZERO)
    amount_paid = models.DecimalField(
        **MONEY_FIELD, default=ZERO, help_text="Derived from payment allocations; never edited directly."
    )
    payment_status = models.CharField(max_length=10, choices=PaymentStatus.choices, default=PaymentStatus.UNPAID, db_index=True)
    status = models.CharField(max_length=10, choices=DocumentStatus.choices, default=DocumentStatus.ACTIVE, db_index=True)
    notes = models.TextField(blank=True, default="")
    cancelled_at = models.DateTimeField(null=True, blank=True)
    cancel_reason = models.CharField(max_length=255, blank=True, default="")
    created_by = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.PROTECT, related_name="+")
    cancelled_by = models.ForeignKey(settings.AUTH_USER_MODEL, null=True, blank=True, on_delete=models.PROTECT, related_name="+")

    class Meta:
        abstract = True

    @property
    def balance_due(self) -> Decimal:
        if self.status == DocumentStatus.CANCELLED:
            return money(ZERO)
        return money(self.total_amount - self.amount_paid)

    @property
    def total_discount(self) -> Decimal:
        return money(self.item_discount_total + self.discount_amount)

    @staticmethod
    def document_constraints(prefix: str) -> list:
        return [
            models.CheckConstraint(condition=Q(subtotal__gte=0), name=f"{prefix}_subtotal_gte_0"),
            models.CheckConstraint(condition=Q(item_discount_total__gte=0), name=f"{prefix}_item_discount_gte_0"),
            models.CheckConstraint(condition=Q(discount_value__gte=0), name=f"{prefix}_discount_value_gte_0"),
            models.CheckConstraint(condition=Q(discount_amount__gte=0), name=f"{prefix}_discount_amount_gte_0"),
            models.CheckConstraint(condition=Q(taxable_amount__gte=0), name=f"{prefix}_taxable_gte_0"),
            models.CheckConstraint(condition=Q(tax_amount__gte=0), name=f"{prefix}_tax_gte_0"),
            models.CheckConstraint(condition=Q(total_amount__gte=0), name=f"{prefix}_total_gte_0"),
            models.CheckConstraint(
                condition=Q(amount_paid__gte=0) & Q(amount_paid__lte=models.F("total_amount")),
                name=f"{prefix}_amount_paid_within_total",
            ),
            models.CheckConstraint(condition=Q(due_date__gte=models.F("date")), name=f"{prefix}_due_after_date"),
        ]


class TradeLine(models.Model):
    quantity = models.DecimalField(**QTY_FIELD)
    gross_amount = models.DecimalField(**MONEY_FIELD)
    discount_type = models.CharField(max_length=12, choices=DiscountType.choices, null=True, blank=True)
    discount_value = models.DecimalField(**MONEY_FIELD, default=ZERO)
    discount_amount = models.DecimalField(**MONEY_FIELD, default=ZERO, help_text="Item-level discount amount.")
    invoice_discount_share = models.DecimalField(**MONEY_FIELD, default=ZERO)
    taxable_amount = models.DecimalField(**MONEY_FIELD)
    tax_rate = models.DecimalField(**RATE_FIELD)
    tax_amount = models.DecimalField(**MONEY_FIELD)

    class Meta:
        abstract = True

    @staticmethod
    def line_constraints(prefix: str, price_field: str, total_field: str) -> list:
        return [
            models.CheckConstraint(condition=Q(quantity__gt=0), name=f"{prefix}_quantity_gt_0"),
            models.CheckConstraint(condition=Q(**{f"{price_field}__gte": 0}), name=f"{prefix}_price_gte_0"),
            models.CheckConstraint(condition=Q(gross_amount__gte=0), name=f"{prefix}_gross_gte_0"),
            models.CheckConstraint(condition=Q(discount_value__gte=0), name=f"{prefix}_discount_value_gte_0"),
            models.CheckConstraint(condition=Q(discount_amount__gte=0), name=f"{prefix}_discount_gte_0"),
            models.CheckConstraint(condition=Q(invoice_discount_share__gte=0), name=f"{prefix}_inv_discount_gte_0"),
            models.CheckConstraint(condition=Q(taxable_amount__gte=0), name=f"{prefix}_taxable_gte_0"),
            models.CheckConstraint(condition=Q(tax_rate__gte=0) & Q(tax_rate__lte=100), name=f"{prefix}_tax_rate_0_100"),
            models.CheckConstraint(condition=Q(tax_amount__gte=0), name=f"{prefix}_tax_gte_0"),
            models.CheckConstraint(condition=Q(**{f"{total_field}__gte": 0}), name=f"{prefix}_total_gte_0"),
        ]


def payment_status_for(total: Decimal, paid: Decimal) -> str:
    if paid <= 0:
        return PaymentStatus.UNPAID if total > 0 else PaymentStatus.PAID
    if paid >= total:
        return PaymentStatus.PAID
    return PaymentStatus.PARTIAL


# ---------------------------------------------------------------------------
# Input serializers shared by sales & purchases (no totals are accepted)
# ---------------------------------------------------------------------------
class LineInputSerializer(serializers.Serializer):
    product = serializers.IntegerField(min_value=1)
    quantity = serializers.DecimalField(max_digits=14, decimal_places=3, min_value=Decimal("0.001"))
    unit_price = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0"), required=False, allow_null=True)
    discount_type = serializers.ChoiceField(choices=DiscountType.choices, required=False, allow_null=True, allow_blank=True)
    discount_value = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0"), required=False, default=ZERO)


class PaymentInputSerializer(serializers.Serializer):
    """Payment recorded together with a sale/purchase.

    ``pay_in_full`` lets the server use its own calculated total, so the
    browser never has to supply the amount for a fully paid document.
    """

    pay_in_full = serializers.BooleanField(required=False, default=False)
    amount = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0.01"), required=False, allow_null=True)
    payment_method = serializers.ChoiceField(choices=PaymentMethod.choices, required=False, default=PaymentMethod.CASH)
    reference_number = serializers.CharField(max_length=60, required=False, allow_blank=True, default="")

    def validate(self, attrs):
        if not attrs.get("pay_in_full") and not attrs.get("amount"):
            raise serializers.ValidationError({"amount": "Enter the amount paid."})
        return attrs


class DocumentInputSerializer(serializers.Serializer):
    party = serializers.IntegerField(min_value=1)
    date = serializers.DateField()
    due_date = serializers.DateField(required=False, allow_null=True)
    discount_type = serializers.ChoiceField(choices=DiscountType.choices, required=False, allow_null=True, allow_blank=True)
    discount_value = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0"), required=False, default=ZERO)
    notes = serializers.CharField(required=False, allow_blank=True, default="", max_length=2000)
    items = LineInputSerializer(many=True)
    payment = PaymentInputSerializer(required=False, allow_null=True)

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        if self.context.get("preview"):
            # Totals can be previewed before a party is chosen.
            self.fields["party"].required = False

    def validate_items(self, value):
        if not value:
            raise serializers.ValidationError("Add at least one item.")
        if len(value) > 500:
            raise serializers.ValidationError("A document can contain at most 500 lines.")
        return value

    def validate(self, attrs):
        if attrs.get("due_date") and attrs["due_date"] < attrs["date"]:
            raise serializers.ValidationError({"due_date": "Due date cannot be before the document date."})
        for key in ("discount_type",):
            if attrs.get(key) == "":
                attrs[key] = None
        for item in attrs["items"]:
            if item.get("discount_type") == "":
                item["discount_type"] = None
        return attrs


def build_totals(items: list[dict], products: dict, *, price_attr: str, discount_type, discount_value) -> DocumentTotals:
    """Turn validated line input into calculated totals.

    The unit price defaults to the product's price; the tax rate always comes
    from the product master (never from the client).
    """
    lines = []
    for item in items:
        product = products[item["product"]]
        price = item.get("unit_price")
        lines.append(
            LineInput(
                quantity=item["quantity"],
                unit_price=getattr(product, price_attr) if price is None else price,
                tax_rate=product.tax_rate,
                discount_type=item.get("discount_type") or None,
                discount_value=item.get("discount_value") or ZERO,
                ref=product,
            )
        )
    return calculate_document(lines, discount_type or None, discount_value or ZERO)


def resolve_due_date(*, date: dt.date, requested: dt.date | None, credit_days: int, user, override_perm: str) -> dt.date:
    from apps.users.permissions import has_perm

    default = date + dt.timedelta(days=credit_days or 0)
    if requested is None or requested == default:
        return default
    if not has_perm(user, override_perm):
        raise BusinessError(
            "You are not allowed to override the due date calculated from the credit terms.",
            code="DUE_DATE_OVERRIDE_DENIED",
            status_code=403,
        )
    return requested


def apply_totals(document: TradeDocument, totals: DocumentTotals) -> None:
    document.subtotal = totals.subtotal
    document.item_discount_total = totals.item_discount_total
    document.discount_type = totals.discount_type
    document.discount_value = totals.discount_value
    document.discount_amount = totals.discount_amount
    document.taxable_amount = totals.taxable_amount
    document.tax_amount = totals.tax_amount
    document.total_amount = totals.total_amount


def serialize_totals(totals: DocumentTotals, stock: dict | None = None) -> dict:
    data = totals.as_dict()
    for line, result in zip(data["lines"], totals.lines, strict=True):
        product = result.ref
        line.update(
            {
                "product": product.pk,
                "product_name": product.name,
                "sku_code": product.sku_code,
                "unit": product.get_unit_display(),
            }
        )
        if stock is not None:
            line["available_stock"] = stock.get(product.pk)
    return serializers.JSONField().to_representation(_stringify(data))


def _stringify(value):
    if isinstance(value, Decimal):
        return str(value)
    if isinstance(value, dict):
        return {k: _stringify(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_stringify(v) for v in value]
    return value
