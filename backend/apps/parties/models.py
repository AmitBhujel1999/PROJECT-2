from decimal import Decimal

from django.db import models
from django.db.models import Q

from apps.common.models import TimeStampedModel
from apps.common.money import MONEY_FIELD


class PartyType(models.TextChoices):
    CUSTOMER = "CUSTOMER", "Customer"
    VENDOR = "VENDOR", "Vendor"


class CreditTerms(models.TextChoices):
    CASH = "CASH", "Cash"
    DAYS_7 = "DAYS_7", "7 Days"
    DAYS_15 = "DAYS_15", "15 Days"
    DAYS_30 = "DAYS_30", "30 Days"
    DAYS_45 = "DAYS_45", "45 Days"
    DAYS_60 = "DAYS_60", "60 Days"
    DAYS_90 = "DAYS_90", "90 Days"
    CUSTOM = "CUSTOM", "Custom"


CREDIT_TERM_DAYS = {
    CreditTerms.CASH: 0,
    CreditTerms.DAYS_7: 7,
    CreditTerms.DAYS_15: 15,
    CreditTerms.DAYS_30: 30,
    CreditTerms.DAYS_45: 45,
    CreditTerms.DAYS_60: 60,
    CreditTerms.DAYS_90: 90,
}


class Party(TimeStampedModel):
    """A customer or a vendor.

    Balances are intentionally NOT stored here: receivables/payables are always
    derived from the underlying invoices, bills, receipts and payments.
    """

    type = models.CharField(max_length=10, choices=PartyType.choices, db_index=True)
    name = models.CharField(max_length=200, db_index=True)
    pan_vat_no = models.CharField("PAN/VAT No.", max_length=30, blank=True, default="", db_index=True)
    phone = models.CharField(max_length=30, blank=True, default="", db_index=True)
    email = models.EmailField(blank=True, default="")
    address = models.TextField(blank=True, default="")
    credit_terms = models.CharField(max_length=10, choices=CreditTerms.choices, default=CreditTerms.CASH)
    credit_days = models.PositiveIntegerField(default=0)
    credit_limit = models.DecimalField(**MONEY_FIELD, default=Decimal("0"))
    notes = models.TextField(blank=True, default="")
    is_active = models.BooleanField(default=True, db_index=True)

    class Meta:
        ordering = ["name"]
        verbose_name_plural = "parties"
        indexes = [models.Index(fields=["type", "name"])]
        constraints = [
            models.CheckConstraint(condition=Q(credit_limit__gte=0), name="party_credit_limit_gte_0"),
            models.CheckConstraint(condition=Q(credit_days__lte=3650), name="party_credit_days_lte_3650"),
        ]

    def __str__(self):
        return self.name

    def save(self, *args, **kwargs):
        if self.credit_terms != CreditTerms.CUSTOM:
            self.credit_days = CREDIT_TERM_DAYS[self.credit_terms]
        self.name = (self.name or "").strip()
        super().save(*args, **kwargs)
