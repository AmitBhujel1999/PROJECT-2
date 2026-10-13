"""Journal services: chart of accounts, journal vouchers, ledger books and the
journal amounts other modules (cash book, party balances, expense report) add in.

Balances are signed with debit positive; ``dr_cr`` turns them into "1,000.00 Dr".
"""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

from django.db import transaction
from django.db.models import Q, Sum
from django.utils import timezone

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot
from apps.common.exceptions import BusinessError
from apps.common.models import DocumentSequence, DocumentStatus
from apps.common.money import ZERO, money

from .models import AccountGroup, JournalLine, JournalVoucher, LedgerAccount, Side

NIL = Decimal("0.00")
KINDS = {"ledger": "Ledger", "cash": "Cash/Bank", "party": "Party", "expense": "Expense category"}


def signed_opening(ledger: LedgerAccount) -> Decimal:
    return ledger.opening_balance if ledger.opening_side == Side.DR else -ledger.opening_balance


def dr_cr(value: Decimal) -> dict:
    """Signed balance -> {"amount": "1000.00", "side": "Dr"}."""
    return {"amount": money(abs(value)), "side": "Dr" if value >= 0 else "Cr"}


def active_lines():
    return JournalLine.objects.filter(voucher__status=DocumentStatus.ACTIVE)


# ---------------------------------------------------------------------------
# Chart of accounts
# ---------------------------------------------------------------------------
def _ensure_unique_name(name: str, exclude_pk=None) -> None:
    qs = LedgerAccount.objects.filter(name__iexact=name.strip())
    if exclude_pk:
        qs = qs.exclude(pk=exclude_pk)
    if qs.exists():
        raise BusinessError(f"A ledger named {name.strip()} already exists.", code="DUPLICATE_LEDGER")


@transaction.atomic
def create_ledger(*, data: dict, user) -> LedgerAccount:
    _ensure_unique_name(data["name"])
    ledger = LedgerAccount.objects.create(**data)
    record(AuditAction.CREATE, ledger, after=snapshot(ledger), user=user)
    return ledger


@transaction.atomic
def update_ledger(ledger: LedgerAccount, *, data: dict, user) -> LedgerAccount:
    ledger = LedgerAccount.objects.select_for_update().get(pk=ledger.pk)
    if "name" in data:
        _ensure_unique_name(data["name"], exclude_pk=ledger.pk)
    before = snapshot(ledger)
    for key, value in data.items():
        setattr(ledger, key, value)
    ledger.save()
    record(AuditAction.UPDATE, ledger, before=before, after=snapshot(ledger), user=user)
    return ledger


@transaction.atomic
def set_ledger_active(ledger: LedgerAccount, *, active: bool, user) -> LedgerAccount:
    before = snapshot(ledger)
    ledger.is_active = active
    ledger.save(update_fields=["is_active", "updated_at"])
    record(AuditAction.ACTIVATE if active else AuditAction.DEACTIVATE, ledger, before=before, after=snapshot(ledger), user=user)
    return ledger


@transaction.atomic
def delete_ledger(ledger: LedgerAccount, *, user) -> None:
    if ledger.journal_lines.exists():
        raise BusinessError(
            "This ledger has journal entries and cannot be deleted. Deactivate it instead.", code="LEDGER_IN_USE", status_code=409
        )
    record(AuditAction.DELETE, ledger, before=snapshot(ledger), user=user)
    ledger.delete()


def ledger_balances(as_of: dt.date | None = None) -> dict[int, Decimal]:
    """Signed (Dr +) balance of every chart-of-accounts ledger."""
    result = {lg.pk: signed_opening(lg) for lg in LedgerAccount.objects.all()}
    lines = active_lines().filter(ledger__isnull=False)
    if as_of:
        lines = lines.filter(voucher__date__lte=as_of)
    for row in lines.values("ledger_id").annotate(dr=Sum("debit"), cr=Sum("credit")).order_by():
        result[row["ledger_id"]] += row["dr"] - row["cr"]
    return {k: money(v) for k, v in result.items()}


def ledger_book(ledger: LedgerAccount, *, start: dt.date | None = None, end: dt.date | None = None) -> dict:
    end = end or timezone.localdate()
    lines = active_lines().filter(ledger=ledger)
    opening = signed_opening(ledger)
    if start:
        before = lines.filter(voucher__date__lt=start).aggregate(dr=Sum("debit"), cr=Sum("credit"))
        opening += (before["dr"] or ZERO) - (before["cr"] or ZERO)
        lines = lines.filter(voucher__date__gte=start)
    lines = lines.filter(voucher__date__lte=end).select_related("voucher").order_by("voucher__date", "voucher__id", "line_no")
    running = opening
    total_dr = total_cr = NIL
    entries = []
    for line in lines:
        running += line.debit - line.credit
        total_dr += line.debit
        total_cr += line.credit
        entries.append(
            {
                "date": line.voucher.date,
                "voucher_id": line.voucher_id,
                "number": line.voucher.voucher_number,
                "narration": line.narration or line.voucher.narration,
                "debit": money(line.debit),
                "credit": money(line.credit),
                "balance": dr_cr(running),
            }
        )
    return {
        "ledger": ledger.pk,
        "ledger_name": ledger.name,
        "group_display": ledger.get_group_display(),
        "opening": dr_cr(opening),
        "total_debit": money(total_dr),
        "total_credit": money(total_cr),
        "closing": dr_cr(running),
        "start_date": start,
        "end_date": end,
        "entries": entries,
    }


# ---------------------------------------------------------------------------
# Accounts a journal line can post to
# ---------------------------------------------------------------------------
def account_options(search: str = "", limit: int = 20) -> list[dict]:
    """Ledgers, cash/bank accounts, customers, vendors and expense categories in one searchable list."""
    from apps.cashbank.models import MoneyAccount
    from apps.expenses.models import ExpenseCategory
    from apps.parties.models import Party

    search = search.strip()

    def named(qs, field="name"):
        return (qs.filter(**{f"{field}__icontains": search}) if search else qs).filter(is_active=True).order_by(field)[:limit]

    options = [
        {"key": f"ledger:{lg.pk}", "name": lg.name, "group": lg.get_group_display()}
        for lg in named(LedgerAccount.objects.all())
    ]
    options += [
        {"key": f"cash:{a.pk}", "name": a.name, "group": f"Cash/Bank · {a.get_kind_display()}"}
        for a in named(MoneyAccount.objects.all())
    ]
    options += [
        {"key": f"party:{p.pk}", "name": p.name, "group": p.get_type_display()}
        for p in named(Party.objects.all())
    ]
    options += [
        {"key": f"expense:{c.pk}", "name": c.name, "group": "Expense category"}
        for c in named(ExpenseCategory.objects.all())
    ]
    return options


def _target(key: str) -> dict:
    """"ledger:5" -> {"ledger": <LedgerAccount>} after checking it exists and is active."""
    from apps.cashbank.models import MoneyAccount
    from apps.expenses.models import ExpenseCategory
    from apps.parties.models import Party

    kind, _, raw = (key or "").partition(":")
    models = {"ledger": LedgerAccount, "cash": MoneyAccount, "party": Party, "expense": ExpenseCategory}
    fields = {"ledger": "ledger", "cash": "money_account", "party": "party", "expense": "expense_category"}
    if kind not in models or not raw.isdigit():
        raise BusinessError("Choose an account for every line.", code="ACCOUNT_REQUIRED")
    obj = models[kind].objects.filter(pk=int(raw)).first()
    if obj is None:
        raise BusinessError(f"{KINDS[kind]} not found.", code="ACCOUNT_NOT_FOUND")
    if not obj.is_active:
        raise BusinessError(f"{obj.name} is inactive.", code="ACCOUNT_INACTIVE")
    return {fields[kind]: obj}


def line_account(line: JournalLine) -> dict:
    if line.ledger_id:
        return {"key": f"ledger:{line.ledger_id}", "name": line.ledger.name, "group": line.ledger.get_group_display()}
    if line.money_account_id:
        a = line.money_account
        return {"key": f"cash:{a.pk}", "name": a.name, "group": f"Cash/Bank · {a.get_kind_display()}"}
    if line.party_id:
        return {"key": f"party:{line.party_id}", "name": line.party.name, "group": line.party.get_type_display()}
    return {"key": f"expense:{line.expense_category_id}", "name": line.expense_category.name, "group": "Expense category"}


# ---------------------------------------------------------------------------
# Vouchers
# ---------------------------------------------------------------------------
@transaction.atomic
def create_voucher(*, data: dict, user) -> JournalVoucher:
    raw_lines = data.get("lines") or []
    if len(raw_lines) < 2:
        raise BusinessError("A journal voucher needs at least two lines.", code="TOO_FEW_LINES")
    lines = []
    total_dr = total_cr = NIL
    for i, raw in enumerate(raw_lines, start=1):
        debit, credit = money(raw.get("debit") or 0), money(raw.get("credit") or 0)
        if debit < 0 or credit < 0 or (debit > 0) == (credit > 0):
            raise BusinessError(f"Line {i}: enter either a debit or a credit amount.", code="INVALID_LINE")
        lines.append({"line_no": i, "debit": debit, "credit": credit, "narration": raw.get("narration", ""), **_target(raw.get("account"))})
        total_dr += debit
        total_cr += credit
    if total_dr != total_cr:
        raise BusinessError(
            f"Debit total ({total_dr:,.2f}) and credit total ({total_cr:,.2f}) must be equal.", code="NOT_BALANCED"
        )
    voucher = JournalVoucher.objects.create(
        voucher_number=DocumentSequence.next_number(DocumentSequence.JOURNAL),
        date=data["date"],
        narration=data.get("narration", ""),
        total_amount=total_dr,
        created_by=user,
    )
    JournalLine.objects.bulk_create([JournalLine(voucher=voucher, **line) for line in lines])
    record(AuditAction.CREATE, voucher, after=snapshot(voucher), user=user)
    return voucher


@transaction.atomic
def cancel_voucher(voucher: JournalVoucher, *, reason: str, user) -> JournalVoucher:
    voucher = JournalVoucher.objects.select_for_update().get(pk=voucher.pk)
    if voucher.status == DocumentStatus.CANCELLED:
        raise BusinessError(f"{voucher.voucher_number} is already cancelled.", code="ALREADY_CANCELLED")
    if not reason.strip():
        raise BusinessError("A cancellation reason is required.", code="REASON_REQUIRED")
    before = snapshot(voucher)
    voucher.status = DocumentStatus.CANCELLED
    voucher.cancelled_at = timezone.now()
    voucher.cancelled_by = user
    voucher.cancel_reason = reason.strip()
    voucher.save()
    record(AuditAction.CANCEL, voucher, before=before, after=snapshot(voucher), user=user)
    return voucher


# ---------------------------------------------------------------------------
# Journal amounts used by other modules
# ---------------------------------------------------------------------------
def party_effect(*, party_type: str, document_is_debit: bool, as_of: dt.date | None = None, party_id=None,
                 start: dt.date | None = None) -> dict[int, Decimal]:
    """Net change in what each party owes (receivable) or is owed (payable) from journal lines.

    Customers: a debit increases what they owe. Vendors: a credit increases what we owe.
    """
    lines = active_lines().filter(party__type=party_type)
    if party_id:
        lines = lines.filter(party_id=party_id)
    if as_of:
        lines = lines.filter(voucher__date__lte=as_of)
    if start:
        lines = lines.filter(voucher__date__gte=start)
    sign = 1 if document_is_debit else -1
    return {
        row["party_id"]: money(sign * (row["dr"] - row["cr"]))
        for row in lines.values("party_id").annotate(dr=Sum("debit"), cr=Sum("credit")).order_by()
    }


def party_lines(party_id: int, *, start: dt.date | None, end: dt.date):
    q = Q(party_id=party_id, voucher__date__lte=end)
    if start:
        q &= Q(voucher__date__gte=start)
    return active_lines().filter(q).select_related("voucher")


def expense_category_totals(*, start: dt.date | None = None, end: dt.date | None = None, category_id=None) -> dict[int, Decimal]:
    lines = active_lines().filter(expense_category__isnull=False)
    if start:
        lines = lines.filter(voucher__date__gte=start)
    if end:
        lines = lines.filter(voucher__date__lte=end)
    if category_id:
        lines = lines.filter(expense_category_id=category_id)
    return {
        row["expense_category_id"]: money(row["dr"] - row["cr"])
        for row in lines.values("expense_category_id").annotate(dr=Sum("debit"), cr=Sum("credit")).order_by()
    }


GROUP_ORDER = [g.value for g in AccountGroup]
