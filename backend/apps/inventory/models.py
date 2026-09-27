from decimal import Decimal

from django.conf import settings
from django.db import models
from django.db.models import Q

from apps.common.money import MONEY_FIELD, QTY_FIELD


class MovementType(models.TextChoices):
    OPENING_STOCK = "OPENING_STOCK", "Opening stock"
    PURCHASE = "PURCHASE", "Purchase"
    SALE = "SALE", "Sale"
    ADJUSTMENT = "ADJUSTMENT", "Adjustment"
    PURCHASE_CANCEL = "PURCHASE_CANCEL", "Purchase cancellation"
    SALE_CANCEL = "SALE_CANCEL", "Sale cancellation"


class ImmutableQuerySet(models.QuerySet):
    def update(self, **kwargs):
        raise RuntimeError("Stock ledger entries are immutable.")

    def delete(self):
        raise RuntimeError("Stock ledger entries are immutable.")


class StockMovement(models.Model):
    """Immutable stock ledger.

    Every stock-affecting event appends exactly one row. Rows are never
    updated or deleted (enforced here and by a PostgreSQL trigger); a
    cancellation appends a reversing row instead.

    ``running_balance`` is the product's balance in *posting order* (the order
    rows were written, protected by a row lock on the product). The CHECK
    constraint on it makes negative inventory impossible at the database level.
    Historical balances by business date are computed from quantity_in/out.
    """

    product = models.ForeignKey("products.Product", on_delete=models.PROTECT, related_name="stock_movements")
    date = models.DateField(db_index=True)
    transaction_type = models.CharField(max_length=20, choices=MovementType.choices, db_index=True)
    reference_type = models.CharField(max_length=30, blank=True, default="")
    reference_id = models.BigIntegerField(null=True, blank=True)
    reference_number = models.CharField(max_length=40, blank=True, default="", db_index=True)
    quantity_in = models.DecimalField(**QTY_FIELD, default=Decimal("0"))
    quantity_out = models.DecimalField(**QTY_FIELD, default=Decimal("0"))
    running_balance = models.DecimalField(**QTY_FIELD)
    unit_cost = models.DecimalField(**MONEY_FIELD, default=Decimal("0"))
    notes = models.CharField(max_length=255, blank=True, default="")
    created_at = models.DateTimeField(auto_now_add=True)
    created_by = models.ForeignKey(settings.AUTH_USER_MODEL, null=True, blank=True, on_delete=models.PROTECT, related_name="+")

    objects = ImmutableQuerySet.as_manager()

    class Meta:
        ordering = ["date", "id"]
        indexes = [
            models.Index(fields=["product", "date", "id"], name="stockmove_product_date_idx"),
            models.Index(fields=["product", "-id"], name="stockmove_product_latest_idx"),
            models.Index(fields=["reference_type", "reference_id"], name="stockmove_reference_idx"),
        ]
        constraints = [
            models.CheckConstraint(condition=Q(quantity_in__gte=0), name="stockmove_qty_in_gte_0"),
            models.CheckConstraint(condition=Q(quantity_out__gte=0), name="stockmove_qty_out_gte_0"),
            models.CheckConstraint(
                condition=(Q(quantity_in__gt=0, quantity_out=0) | Q(quantity_out__gt=0, quantity_in=0)),
                name="stockmove_exactly_one_direction",
            ),
            models.CheckConstraint(condition=Q(running_balance__gte=0), name="stockmove_no_negative_stock"),
        ]

    def __str__(self):
        return f"{self.date} {self.transaction_type} {self.product_id} +{self.quantity_in} -{self.quantity_out}"

    def save(self, *args, **kwargs):
        if self.pk is not None:
            raise RuntimeError("Stock ledger entries are immutable.")
        super().save(*args, **kwargs)

    def delete(self, *args, **kwargs):
        raise RuntimeError("Stock ledger entries are immutable.")


class AdjustmentReason(models.TextChoices):
    DAMAGED = "DAMAGED", "Damaged goods"
    EXPIRED = "EXPIRED", "Expired"
    LOST = "LOST", "Lost / theft"
    COUNT_CORRECTION = "COUNT_CORRECTION", "Physical count correction"
    FOUND = "FOUND", "Found / surplus"
    RETURN = "RETURN", "Returned goods"
    OTHER = "OTHER", "Other"


class StockAdjustment(models.Model):
    adjustment_number = models.CharField(max_length=20, unique=True)
    product = models.ForeignKey("products.Product", on_delete=models.PROTECT, related_name="stock_adjustments")
    date = models.DateField(db_index=True)
    quantity = models.DecimalField(**QTY_FIELD, help_text="Positive increases stock, negative decreases it.")
    reason = models.CharField(max_length=20, choices=AdjustmentReason.choices)
    notes = models.CharField(max_length=255, blank=True, default="")
    stock_before = models.DecimalField(**QTY_FIELD)
    stock_after = models.DecimalField(**QTY_FIELD)
    created_at = models.DateTimeField(auto_now_add=True)
    created_by = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.PROTECT, related_name="+")

    class Meta:
        ordering = ["-date", "-id"]
        constraints = [models.CheckConstraint(condition=~Q(quantity=0), name="adjustment_quantity_not_zero")]

    def __str__(self):
        return self.adjustment_number
