from django.conf import settings
from django.db import models
from django.db.models import F, Q
from django.db.models.functions import Lower

from apps.common.models import DocumentStatus, PaymentMethod, TimeStampedModel
from apps.common.money import MONEY_FIELD, RATE_FIELD, ZERO


class ExpenseCategory(TimeStampedModel):
    """Groups expenses for reporting (Rent, Salaries, Utilities, ...).

    Categories with expenses are deactivated, never deleted.
    """

    name = models.CharField(max_length=100)
    description = models.CharField(max_length=255, blank=True, default="")
    is_active = models.BooleanField(default=True, db_index=True)

    class Meta:
        ordering = ["name"]
        verbose_name_plural = "expense categories"
        constraints = [models.UniqueConstraint(Lower("name"), name="expensecategory_name_ci_unique")]

    def __str__(self):
        return self.name

    def save(self, *args, **kwargs):
        self.name = (self.name or "").strip()
        super().save(*args, **kwargs)


class Expense(TimeStampedModel):
    """A business expense paid at the time it is recorded (EXP-000001).

    ``amount`` is entered excluding tax; ``tax_amount`` and ``total_amount``
    are calculated by the server. Expenses are never deleted — they are
    cancelled, which keeps the number and the audit trail.
    """

    expense_number = models.CharField(max_length=20, unique=True)
    date = models.DateField(db_index=True)
    category = models.ForeignKey(ExpenseCategory, on_delete=models.PROTECT, related_name="expenses")
    vendor = models.ForeignKey(
        "parties.Party", null=True, blank=True, on_delete=models.PROTECT, related_name="expenses",
        help_text="Optional: the vendor that was paid.",
    )
    payee = models.CharField(max_length=200, blank=True, default="", help_text="Paid to, when not a registered vendor.")
    description = models.CharField(max_length=255)
    amount = models.DecimalField(**MONEY_FIELD, help_text="Amount excluding tax.")
    tax_rate = models.DecimalField(**RATE_FIELD, default=ZERO)
    tax_amount = models.DecimalField(**MONEY_FIELD, default=ZERO)
    total_amount = models.DecimalField(**MONEY_FIELD)
    payment_method = models.CharField(max_length=10, choices=PaymentMethod.choices, default=PaymentMethod.CASH)
    reference_number = models.CharField(max_length=60, blank=True, default="", help_text="Bill no., cheque no., bank ref, etc.")
    notes = models.TextField(blank=True, default="")
    status = models.CharField(max_length=10, choices=DocumentStatus.choices, default=DocumentStatus.ACTIVE, db_index=True)
    cancelled_at = models.DateTimeField(null=True, blank=True)
    cancel_reason = models.CharField(max_length=255, blank=True, default="")
    created_by = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.PROTECT, related_name="+")
    cancelled_by = models.ForeignKey(settings.AUTH_USER_MODEL, null=True, blank=True, on_delete=models.PROTECT, related_name="+")

    class Meta:
        ordering = ["-date", "-id"]
        indexes = [models.Index(fields=["category", "date"]), models.Index(fields=["vendor", "date"])]
        constraints = [
            models.CheckConstraint(condition=Q(amount__gt=0), name="expense_amount_gt_0"),
            models.CheckConstraint(condition=Q(tax_rate__gte=0) & Q(tax_rate__lte=100), name="expense_tax_rate_0_100"),
            models.CheckConstraint(condition=Q(tax_amount__gte=0), name="expense_tax_gte_0"),
            models.CheckConstraint(
                condition=Q(total_amount=F("amount") + F("tax_amount")), name="expense_total_is_amount_plus_tax"
            ),
        ]

    def __str__(self):
        return self.expense_number

    @property
    def paid_to(self) -> str:
        return self.vendor.name if self.vendor_id else self.payee
