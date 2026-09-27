from decimal import Decimal

from django.db import models
from django.db.models import Q

from apps.common.models import TimeStampedModel
from apps.common.money import MONEY_FIELD, QTY_FIELD, RATE_FIELD


class Unit(models.TextChoices):
    PCS = "PCS", "Pcs"
    KG = "KG", "Kg"
    BOX = "BOX", "Box"
    LTR = "LTR", "Ltr"
    METER = "METER", "Meter"
    SET = "SET", "Set"
    PACK = "PACK", "Pack"
    DOZEN = "DOZEN", "Dozen"


def default_tax_rate() -> Decimal:
    from apps.common.models import BusinessSettings

    try:
        return BusinessSettings.get_solo().default_tax_rate
    except Exception:  # pragma: no cover - during initial migrations
        return Decimal("13.00")


class Product(TimeStampedModel):
    name = models.CharField(max_length=200, db_index=True)
    sku_code = models.CharField("SKU", max_length=64, unique=True, db_index=True)
    unit = models.CharField(max_length=10, choices=Unit.choices, default=Unit.PCS)
    description = models.TextField(blank=True, default="")
    opening_stock = models.DecimalField(**QTY_FIELD, default=Decimal("0"))
    reorder_level = models.DecimalField(**QTY_FIELD, default=Decimal("0"))
    purchase_price = models.DecimalField(**MONEY_FIELD, default=Decimal("0"))
    selling_price = models.DecimalField(**MONEY_FIELD, default=Decimal("0"))
    tax_rate = models.DecimalField(**RATE_FIELD, default=default_tax_rate)
    is_active = models.BooleanField(default=True, db_index=True)

    class Meta:
        ordering = ["name"]
        constraints = [
            models.CheckConstraint(condition=Q(opening_stock__gte=0), name="product_opening_stock_gte_0"),
            models.CheckConstraint(condition=Q(reorder_level__gte=0), name="product_reorder_level_gte_0"),
            models.CheckConstraint(condition=Q(purchase_price__gte=0), name="product_purchase_price_gte_0"),
            models.CheckConstraint(condition=Q(selling_price__gte=0), name="product_selling_price_gte_0"),
            models.CheckConstraint(condition=Q(tax_rate__gte=0) & Q(tax_rate__lte=100), name="product_tax_rate_0_100"),
        ]

    def __str__(self):
        return f"{self.name} ({self.sku_code})"

    def save(self, *args, **kwargs):
        self.sku_code = (self.sku_code or "").strip().upper()
        self.name = (self.name or "").strip()
        super().save(*args, **kwargs)
