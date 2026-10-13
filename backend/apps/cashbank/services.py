"""Cash & bank services: accounts, the account book (cash/bank book), contra entries and the day book."""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

from django.db import transaction
from django.db.models import Sum
from django.utils import timezone

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot
from apps.common.exceptions import BusinessError
from apps.common.models import DocumentSequence, DocumentStatus, PaymentMethod
from apps.common.money import ZERO, money

from .models import METHOD_FOR_KIND, AccountKind, Contra, MoneyAccount

NIL = Decimal("0.00")
DEFAULT_NAMES = {AccountKind.CASH: "Cash in Hand", AccountKind.BANK: "Bank"}


# ---------------------------------------------------------------------------
# Accounts
# ---------------------------------------------------------------------------
def _ensure_unique_name(name: str, exclude_pk=None) -> None:
    qs = MoneyAccount.objects.filter(name__iexact=name.strip())
    if exclude_pk:
        qs = qs.exclude(pk=exclude_pk)
    if qs.exists():
        raise BusinessError(f"An account named {name.strip()} already exists.", code="DUPLICATE_ACCOUNT")


@transaction.atomic
def create_account(*, data: dict, user) -> MoneyAccount:
    _ensure_unique_name(data["name"])
    account = MoneyAccount.objects.create(**data)
    record(AuditAction.CREATE, account, after=snapshot(account), user=user)
    return account


@transaction.atomic
def update_account(account: MoneyAccount, *, data: dict, user) -> MoneyAccount:
    account = MoneyAccount.objects.select_for_update().get(pk=account.pk)
    if "name" in data:
        _ensure_unique_name(data["name"], exclude_pk=account.pk)
    before = snapshot(account)
    for key, value in data.items():
        setattr(account, key, value)
    account.save()
    record(AuditAction.UPDATE, account, before=before, after=snapshot(account), user=user)
    return account


@transaction.atomic
def set_account_active(account: MoneyAccount, *, active: bool, user) -> MoneyAccount:
    before = snapshot(account)
    account.is_active = active
    account.save(update_fields=["is_active", "updated_at"])
    record(AuditAction.ACTIVATE if active else AuditAction.DEACTIVATE, account, before=before, after=snapshot(account), user=user)
    return account


def _used(account: MoneyAccount) -> bool:
    from apps.expenses.models import Expense
    from apps.payables.models import VendorPayment
    from apps.receivables.models import CustomerReceipt

    return (
        CustomerReceipt.objects.filter(account=account).exists()
        or VendorPayment.objects.filter(account=account).exists()
        or Expense.objects.filter(account=account).exists()
        or Contra.objects.filter(from_account=account).exists()
        or Contra.objects.filter(to_account=account).exists()
        or account.journal_lines.exists()
    )


@transaction.atomic
def delete_account(account: MoneyAccount, *, user) -> None:
    """Physically delete only accounts that were never used."""
    if _used(account):
        raise BusinessError(
            "This account has entries and cannot be deleted. Deactivate it instead.", code="ACCOUNT_IN_USE", status_code=409
        )
    record(AuditAction.DELETE, account, before=snapshot(account), user=user)
    account.delete()


def default_account(kind: str) -> MoneyAccount:
    """First active account of ``kind``; creates "Cash in Hand" / "Bank" when there is none."""
    account = MoneyAccount.objects.filter(kind=kind, is_active=True).order_by("id").first()
    if account is None:
        name = DEFAULT_NAMES.get(kind, AccountKind(kind).label)
        account = MoneyAccount.objects.filter(name__iexact=name).first() or MoneyAccount.objects.create(name=name, kind=kind)
    return account


def resolve_account(account_id: int | None, payment_method: str | None = None) -> tuple[MoneyAccount, str]:
    """Account and payment method for a receipt/payment/expense.

    With an account, the method follows the account's kind. Without one (older
    API callers), cash goes to the cash account and everything else to the bank.
    """
    if account_id:
        account = MoneyAccount.objects.filter(pk=account_id).first()
        if account is None:
            raise BusinessError("Cash/bank account not found.", code="ACCOUNT_NOT_FOUND")
        if not account.is_active:
            raise BusinessError(f"Account {account.name} is inactive.", code="ACCOUNT_INACTIVE")
        return account, METHOD_FOR_KIND[account.kind]
    method = payment_method or PaymentMethod.CASH
    kind = AccountKind.CASH if method == PaymentMethod.CASH else AccountKind.BANK
    return default_account(kind), method


# ---------------------------------------------------------------------------
# Account book
# ---------------------------------------------------------------------------
def _sources(account_id: int | None = None):
    """(queryset, direction, kind label, number field, amount field) for every money movement."""
    from apps.expenses.models import Expense
    from apps.payables.models import VendorPayment
    from apps.receivables.models import CustomerReceipt

    active = {"status": DocumentStatus.ACTIVE}
    acc = (lambda f: {f: account_id}) if account_id else (lambda f: {})
    return [
        (CustomerReceipt.objects.filter(**active, **acc("account_id")), 1, "Receipt", "receipt_number", "amount"),
        (VendorPayment.objects.filter(**active, **acc("account_id")), -1, "Payment", "payment_number", "amount"),
        (Expense.objects.filter(**active, **acc("account_id")), -1, "Expense", "expense_number", "total_amount"),
        (Contra.objects.filter(**active, **acc("to_account_id")), 1, "Contra", "contra_number", "amount"),
        (Contra.objects.filter(**active, **acc("from_account_id")), -1, "Contra", "contra_number", "amount"),
    ]


def balance(account: MoneyAccount, as_of: dt.date | None = None) -> Decimal:
    """Closing balance of ``account`` at the end of ``as_of`` (today when omitted)."""
    total = account.opening_balance
    for qs, sign, _label, _num, amount_field in _sources(account.pk):
        if as_of:
            qs = qs.filter(date__lte=as_of)
        total += sign * (qs.aggregate(t=Sum(amount_field))["t"] or ZERO)
    lines = _journal_lines(account.pk)
    if as_of:
        lines = lines.filter(voucher__date__lte=as_of)
    j = lines.aggregate(dr=Sum("debit"), cr=Sum("credit"))
    total += (j["dr"] or ZERO) - (j["cr"] or ZERO)
    return money(total)


def _journal_lines(account_id: int | None = None):
    """Journal voucher lines posted to a cash/bank account (debit = money in)."""
    from apps.journal.services import active_lines

    lines = active_lines().filter(money_account__isnull=False)
    return lines.filter(money_account_id=account_id) if account_id else lines


def balances() -> dict[int, Decimal]:
    """Current balance of every account, in a fixed number of queries."""
    result = {a.pk: a.opening_balance for a in MoneyAccount.objects.all()}
    for qs, sign, _label, _num, amount_field in _sources():
        field = "account_id" if qs.model.__name__ != "Contra" else ("to_account_id" if sign > 0 else "from_account_id")
        for row in qs.values(field).annotate(t=Sum(amount_field)):
            result[row[field]] = result.get(row[field], ZERO) + sign * row["t"]
    for row in _journal_lines().values("money_account_id").annotate(dr=Sum("debit"), cr=Sum("credit")).order_by():
        result[row["money_account_id"]] = result.get(row["money_account_id"], ZERO) + row["dr"] - row["cr"]
    return {k: money(v) for k, v in result.items()}


def _particulars(obj, sign: int) -> str:
    name = type(obj).__name__
    if name == "CustomerReceipt":
        return obj.customer.name
    if name == "VendorPayment":
        return obj.vendor.name
    if name == "Expense":
        who = obj.vendor.name if obj.vendor_id else obj.payee
        return f"{obj.category.name}: {obj.description}" + (f" ({who})" if who else "")
    other = obj.from_account if sign > 0 else obj.to_account
    return f"{'Transfer from' if sign > 0 else 'Transfer to'} {other.name}"


_RELATED = {
    "CustomerReceipt": ["customer"],
    "VendorPayment": ["vendor"],
    "Expense": ["category", "vendor"],
    "Contra": ["from_account", "to_account"],
}


def account_book(account: MoneyAccount, *, start: dt.date | None = None, end: dt.date | None = None) -> dict:
    end = end or timezone.localdate()
    opening = balance(account, start - dt.timedelta(days=1)) if start else money(account.opening_balance)
    entries = []
    for qs, sign, label, num_field, amount_field in _sources(account.pk):
        qs = qs.filter(date__lte=end).select_related(*_RELATED[qs.model.__name__])
        if start:
            qs = qs.filter(date__gte=start)
        for obj in qs:
            amount = getattr(obj, amount_field)
            entries.append(
                {
                    "date": obj.date,
                    "voucher_type": label,
                    "number": getattr(obj, num_field),
                    "object_id": obj.pk,
                    "particulars": _particulars(obj, sign),
                    "reference_number": obj.reference_number,
                    "money_in": money(amount) if sign > 0 else NIL,
                    "money_out": money(amount) if sign < 0 else NIL,
                    "_order": obj.created_at.timestamp(),
                }
            )
    lines = _journal_lines(account.pk).filter(voucher__date__lte=end).select_related("voucher")
    if start:
        lines = lines.filter(voucher__date__gte=start)
    for line in lines:
        entries.append(
            {
                "date": line.voucher.date,
                "voucher_type": "Journal",
                "number": line.voucher.voucher_number,
                "object_id": line.voucher_id,
                "particulars": line.narration or line.voucher.narration or "Journal voucher",
                "reference_number": "",
                "money_in": money(line.debit),
                "money_out": money(line.credit),
                "_order": line.voucher.created_at.timestamp() + line.line_no / 1000,
            }
        )
    entries.sort(key=lambda e: (e["date"], e["_order"]))
    running = opening
    total_in = total_out = NIL
    for e in entries:
        e.pop("_order")
        total_in += e["money_in"]
        total_out += e["money_out"]
        running += e["money_in"] - e["money_out"]
        e["balance"] = money(running)
    return {
        "account": account.pk,
        "account_name": account.name,
        "opening_balance": money(opening),
        "total_in": money(total_in),
        "total_out": money(total_out),
        "closing_balance": money(running),
        "start_date": start,
        "end_date": end,
        "entries": entries,
    }


# ---------------------------------------------------------------------------
# Contra entries
# ---------------------------------------------------------------------------
@transaction.atomic
def create_contra(*, data: dict, user) -> Contra:
    accounts = {a.pk: a for a in MoneyAccount.objects.filter(pk__in=[data["from_account"], data["to_account"]])}
    source, target = accounts.get(data["from_account"]), accounts.get(data["to_account"])
    if source is None or target is None:
        raise BusinessError("Cash/bank account not found.", code="ACCOUNT_NOT_FOUND")
    if source.pk == target.pk:
        raise BusinessError("Choose two different accounts.", code="SAME_ACCOUNT")
    for account in (source, target):
        if not account.is_active:
            raise BusinessError(f"Account {account.name} is inactive.", code="ACCOUNT_INACTIVE")
    amount = money(data["amount"])
    if amount <= 0:
        raise BusinessError("Amount must be greater than zero.", code="INVALID_AMOUNT")
    contra = Contra.objects.create(
        contra_number=DocumentSequence.next_number(DocumentSequence.CONTRA),
        date=data["date"],
        from_account=source,
        to_account=target,
        amount=amount,
        reference_number=data.get("reference_number", ""),
        notes=data.get("notes", ""),
        created_by=user,
    )
    record(AuditAction.CREATE, contra, after=snapshot(contra), user=user)
    return contra


@transaction.atomic
def cancel_contra(contra: Contra, *, reason: str, user) -> Contra:
    contra = Contra.objects.select_for_update().get(pk=contra.pk)
    if contra.status == DocumentStatus.CANCELLED:
        raise BusinessError(f"{contra.contra_number} is already cancelled.", code="ALREADY_CANCELLED")
    if not reason.strip():
        raise BusinessError("A cancellation reason is required.", code="REASON_REQUIRED")
    before = snapshot(contra)
    contra.status = DocumentStatus.CANCELLED
    contra.cancelled_at = timezone.now()
    contra.cancelled_by = user
    contra.cancel_reason = reason.strip()
    contra.save()
    record(AuditAction.CANCEL, contra, before=before, after=snapshot(contra), user=user)
    return contra


# ---------------------------------------------------------------------------
# Day book
# ---------------------------------------------------------------------------
def day_book(*, start: dt.date, end: dt.date, include_cancelled=False) -> dict:
    """Every voucher dated between ``start`` and ``end``, in date order."""
    from apps.expenses.models import Expense
    from apps.journal.models import JournalVoucher
    from apps.payables.models import VendorPayment
    from apps.purchases.models import Purchase
    from apps.receivables.models import CustomerReceipt
    from apps.sales.models import Sale

    def rows(qs, label, number, party, amount, account=None, particulars=None, link=None):
        qs = qs.filter(date__gte=start, date__lte=end)
        if not include_cancelled:
            qs = qs.filter(status=DocumentStatus.ACTIVE)
        for obj in qs:
            yield {
                "date": obj.date,
                "voucher_type": label,
                "number": getattr(obj, number),
                "object_id": obj.pk,
                "link": link,
                "particulars": particulars(obj) if particulars else getattr(obj, party).name,
                "account_name": account(obj) if account else "",
                "amount": money(getattr(obj, amount)),
                "status": obj.status,
                "_order": obj.created_at.timestamp(),
            }

    entries = [
        *rows(Sale.objects.select_related("customer"), "Sales", "invoice_number", "customer", "total_amount", link="sales"),
        *rows(Purchase.objects.select_related("vendor"), "Purchase", "bill_number", "vendor", "total_amount", link="purchases"),
        *rows(CustomerReceipt.objects.select_related("customer", "account"), "Receipt", "receipt_number", "customer", "amount",
              account=lambda o: o.account.name, link="receipts"),
        *rows(VendorPayment.objects.select_related("vendor", "account"), "Payment", "payment_number", "vendor", "amount",
              account=lambda o: o.account.name, link="payments"),
        *rows(Expense.objects.select_related("category", "vendor", "account"), "Expense", "expense_number", None, "total_amount",
              account=lambda o: o.account.name, particulars=lambda o: _particulars(o, -1), link="expenses"),
        *rows(Contra.objects.select_related("from_account", "to_account"), "Contra", "contra_number", None, "amount",
              account=lambda o: f"{o.from_account.name} → {o.to_account.name}",
              particulars=lambda o: o.notes or "Transfer between accounts", link="cash-bank/contra"),
        *rows(JournalVoucher.objects.all(), "Journal", "voucher_number", None, "total_amount",
              particulars=lambda o: o.narration or "Journal voucher", link="journal"),
    ]
    entries.sort(key=lambda e: (e["date"], e["_order"]))
    totals: dict[str, Decimal] = {}
    for e in entries:
        e.pop("_order")
        if e["status"] == DocumentStatus.ACTIVE:
            totals[e["voucher_type"]] = totals.get(e["voucher_type"], ZERO) + e["amount"]
    return {
        "start_date": start,
        "end_date": end,
        "count": len(entries),
        "totals": [{"voucher_type": k, "amount": money(v)} for k, v in totals.items()],
        "entries": entries,
    }
