"""Cash and bank accounts the business keeps money in, and transfers between them.

Every receipt, vendor payment and expense names the account the money came
into or went out of, so each account has a cash/bank book with a running
balance. Money moved between two accounts (cash deposited in the bank, cash
withdrawn) is a Contra entry.
"""

from django.conf import settings
from django.db import models
from django.db.models import F, Q

from apps.common.models import DocumentStatus, PaymentMethod, TimeStampedModel
from apps.common.money import MONEY_FIELD, ZERO


class AccountKind(models.TextChoices):
    CASH = "CASH", "Cash"
    BANK = "BANK", "Bank"
    WALLET = "WALLET", "Digital wallet"
    OTHER = "OTHER", "Other"


# Payment method stored on receipts/payments/expenses for each kind of account.
METHOD_FOR_KIND = {
    AccountKind.CASH: PaymentMethod.CASH,
    AccountKind.BANK: PaymentMethod.BANK,
    AccountKind.WALLET: PaymentMethod.ONLINE,
    AccountKind.OTHER: PaymentMethod.OTHER,
}


class MoneyAccount(TimeStampedModel):
    name = models.CharField(max_length=100)
    kind = models.CharField(max_length=10, choices=AccountKind.choices, default=AccountKind.BANK)
    account_number = models.CharField(max_length=60, blank=True, default="")
    opening_balance = models.DecimalField(**MONEY_FIELD, default=ZERO, help_text="Balance before the first entry.")
    notes = models.TextField(blank=True, default="")
    is_active = models.BooleanField(default=True, db_index=True)

    class Meta:
        ordering = ["name"]

    def __str__(self):
        return self.name


class Contra(TimeStampedModel):
    """Money moved from one of the business's accounts to another (CNTR-000001)."""

    contra_number = models.CharField(max_length=20, unique=True)
    date = models.DateField(db_index=True)
    from_account = models.ForeignKey(MoneyAccount, on_delete=models.PROTECT, related_name="contras_out")
    to_account = models.ForeignKey(MoneyAccount, on_delete=models.PROTECT, related_name="contras_in")
    amount = models.DecimalField(**MONEY_FIELD)
    reference_number = models.CharField(max_length=60, blank=True, default="")
    notes = models.TextField(blank=True, default="")
    status = models.CharField(max_length=10, choices=DocumentStatus.choices, default=DocumentStatus.ACTIVE, db_index=True)
    cancelled_at = models.DateTimeField(null=True, blank=True)
    cancel_reason = models.CharField(max_length=255, blank=True, default="")
    created_by = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.PROTECT, related_name="+")
    cancelled_by = models.ForeignKey(settings.AUTH_USER_MODEL, null=True, blank=True, on_delete=models.PROTECT, related_name="+")

    class Meta:
        ordering = ["-date", "-id"]
        constraints = [
            models.CheckConstraint(condition=Q(amount__gt=0), name="contra_amount_gt_0"),
            models.CheckConstraint(condition=~Q(from_account=F("to_account")), name="contra_accounts_differ"),
        ]

    def __str__(self):
        return self.contra_number
