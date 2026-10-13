"""Chart of accounts and journal vouchers (double-entry Dr/Cr adjustments).

A journal line posts to exactly one of:
- a ledger from the chart of accounts (capital, loans, fixed assets, ...),
- a cash/bank account (shows in that account's book),
- a customer or vendor (changes what they owe / are owed),
- an expense category (counts in the expense report).
Every voucher balances: total debit == total credit.
"""

from django.conf import settings
from django.db import models
from django.db.models import Q

from apps.common.models import DocumentStatus, TimeStampedModel
from apps.common.money import MONEY_FIELD, ZERO


class Nature(models.TextChoices):
    ASSET = "ASSET", "Asset"
    LIABILITY = "LIABILITY", "Liability"
    INCOME = "INCOME", "Income"
    EXPENSE = "EXPENSE", "Expense"


class AccountGroup(models.TextChoices):
    CAPITAL = "CAPITAL", "Capital Account"
    RESERVES = "RESERVES", "Reserves & Surplus"
    LOANS = "LOANS", "Loans (Liability)"
    CURRENT_LIABILITIES = "CURRENT_LIABILITIES", "Current Liabilities"
    DUTIES_TAXES = "DUTIES_TAXES", "Duties & Taxes"
    FIXED_ASSETS = "FIXED_ASSETS", "Fixed Assets"
    INVESTMENTS = "INVESTMENTS", "Investments"
    CURRENT_ASSETS = "CURRENT_ASSETS", "Current Assets"
    LOANS_ADVANCES = "LOANS_ADVANCES", "Loans & Advances (Asset)"
    DIRECT_INCOME = "DIRECT_INCOME", "Direct Income"
    INDIRECT_INCOME = "INDIRECT_INCOME", "Indirect Income"
    DIRECT_EXPENSES = "DIRECT_EXPENSES", "Direct Expenses"
    INDIRECT_EXPENSES = "INDIRECT_EXPENSES", "Indirect Expenses"


GROUP_NATURE = {
    AccountGroup.CAPITAL: Nature.LIABILITY,
    AccountGroup.RESERVES: Nature.LIABILITY,
    AccountGroup.LOANS: Nature.LIABILITY,
    AccountGroup.CURRENT_LIABILITIES: Nature.LIABILITY,
    AccountGroup.DUTIES_TAXES: Nature.LIABILITY,
    AccountGroup.FIXED_ASSETS: Nature.ASSET,
    AccountGroup.INVESTMENTS: Nature.ASSET,
    AccountGroup.CURRENT_ASSETS: Nature.ASSET,
    AccountGroup.LOANS_ADVANCES: Nature.ASSET,
    AccountGroup.DIRECT_INCOME: Nature.INCOME,
    AccountGroup.INDIRECT_INCOME: Nature.INCOME,
    AccountGroup.DIRECT_EXPENSES: Nature.EXPENSE,
    AccountGroup.INDIRECT_EXPENSES: Nature.EXPENSE,
}


class Side(models.TextChoices):
    DR = "DR", "Dr"
    CR = "CR", "Cr"


def normal_side(group: str) -> str:
    """Assets and expenses normally carry a debit balance; the rest a credit balance."""
    return Side.DR if GROUP_NATURE[group] in (Nature.ASSET, Nature.EXPENSE) else Side.CR


class LedgerAccount(TimeStampedModel):
    name = models.CharField(max_length=100)
    group = models.CharField(max_length=24, choices=AccountGroup.choices)
    opening_balance = models.DecimalField(**MONEY_FIELD, default=ZERO)
    opening_side = models.CharField(max_length=2, choices=Side.choices, default=Side.DR)
    notes = models.TextField(blank=True, default="")
    is_active = models.BooleanField(default=True, db_index=True)

    class Meta:
        ordering = ["name"]
        constraints = [models.CheckConstraint(condition=Q(opening_balance__gte=0), name="ledger_opening_gte_0")]

    def __str__(self):
        return self.name

    @property
    def nature(self) -> str:
        return GROUP_NATURE[self.group]


class JournalVoucher(TimeStampedModel):
    voucher_number = models.CharField(max_length=20, unique=True)
    date = models.DateField(db_index=True)
    narration = models.TextField(blank=True, default="")
    total_amount = models.DecimalField(**MONEY_FIELD, help_text="Total debit (= total credit).")
    status = models.CharField(max_length=10, choices=DocumentStatus.choices, default=DocumentStatus.ACTIVE, db_index=True)
    cancelled_at = models.DateTimeField(null=True, blank=True)
    cancel_reason = models.CharField(max_length=255, blank=True, default="")
    created_by = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.PROTECT, related_name="+")
    cancelled_by = models.ForeignKey(settings.AUTH_USER_MODEL, null=True, blank=True, on_delete=models.PROTECT, related_name="+")

    class Meta:
        ordering = ["-date", "-id"]
        constraints = [models.CheckConstraint(condition=Q(total_amount__gt=0), name="jv_total_gt_0")]

    def __str__(self):
        return self.voucher_number


class JournalLine(models.Model):
    voucher = models.ForeignKey(JournalVoucher, on_delete=models.CASCADE, related_name="lines")
    line_no = models.PositiveSmallIntegerField()
    ledger = models.ForeignKey(LedgerAccount, null=True, blank=True, on_delete=models.PROTECT, related_name="journal_lines")
    money_account = models.ForeignKey(
        "cashbank.MoneyAccount", null=True, blank=True, on_delete=models.PROTECT, related_name="journal_lines"
    )
    party = models.ForeignKey("parties.Party", null=True, blank=True, on_delete=models.PROTECT, related_name="journal_lines")
    expense_category = models.ForeignKey(
        "expenses.ExpenseCategory", null=True, blank=True, on_delete=models.PROTECT, related_name="journal_lines"
    )
    debit = models.DecimalField(**MONEY_FIELD, default=ZERO)
    credit = models.DecimalField(**MONEY_FIELD, default=ZERO)
    narration = models.CharField(max_length=255, blank=True, default="")

    class Meta:
        ordering = ["voucher", "line_no"]
        constraints = [
            models.CheckConstraint(
                condition=(Q(debit__gt=0) & Q(credit=0)) | (Q(credit__gt=0) & Q(debit=0)),
                name="jvline_one_side",
            ),
            models.CheckConstraint(
                condition=(
                    Q(ledger__isnull=False, money_account__isnull=True, party__isnull=True, expense_category__isnull=True)
                    | Q(ledger__isnull=True, money_account__isnull=False, party__isnull=True, expense_category__isnull=True)
                    | Q(ledger__isnull=True, money_account__isnull=True, party__isnull=False, expense_category__isnull=True)
                    | Q(ledger__isnull=True, money_account__isnull=True, party__isnull=True, expense_category__isnull=False)
                ),
                name="jvline_one_account",
            ),
        ]

    def __str__(self):
        return f"{self.voucher_id}#{self.line_no}"
