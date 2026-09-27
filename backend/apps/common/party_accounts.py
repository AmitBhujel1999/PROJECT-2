"""Generic accounts receivable / accounts payable engine.

Receivables (customers: invoices vs. receipts) and payables (vendors: bills
vs. payments) are mirror images, so the logic is written once here and
parameterised by an :class:`AccountConfig`. Nothing in here stores a party
balance: every figure is derived from documents, payments and allocations.

Sign convention: a positive balance is always "owed on the account" —
the customer owes us (receivable) or we owe the vendor (payable).

Customer ledger:  invoice = Debit,  receipt = Credit
Vendor ledger:    bill    = Credit, payment = Debit
"""

from __future__ import annotations

import datetime as dt
from collections import defaultdict
from dataclasses import dataclass
from decimal import Decimal

from django.db import models, transaction
from django.db.models import Count, F, Max, OuterRef, Q, Subquery, Sum, Value
from django.db.models.functions import Coalesce
from django.utils import timezone

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot

from .documents import payment_status_for
from .exceptions import AllocationError, BusinessError
from .models import DocumentSequence, DocumentStatus
from .money import ZERO, money

MONEY = models.DecimalField(max_digits=20, decimal_places=2)
MZERO = Value(Decimal("0.00"), output_field=MONEY)

AGING_BUCKETS = [
    ("current", "Current / Not Due"),
    ("days_1_30", "1-30 Days"),
    ("days_31_60", "31-60 Days"),
    ("days_61_90", "61-90 Days"),
    ("days_91_120", "91-120 Days"),
    ("days_over_120", "120+ Days"),
]


@dataclass(frozen=True)
class AccountConfig:
    key: str
    party_type: str
    document_model: type[models.Model]
    doc_number: str
    doc_party: str
    doc_label: str
    payment_model: type[models.Model]
    pay_number: str
    pay_party: str
    pay_label: str
    allocation_model: type[models.Model]
    alloc_doc: str
    alloc_pay: str
    doc_alloc_related: str
    sequence_key: str
    document_is_debit: bool

    # -- helpers ---------------------------------------------------------
    def docs(self, party_id=None):
        qs = self.document_model.objects.all()
        return qs.filter(**{f"{self.doc_party}_id": party_id}) if party_id else qs

    def payments(self, party_id=None):
        qs = self.payment_model.objects.all()
        return qs.filter(**{f"{self.pay_party}_id": party_id}) if party_id else qs

    def doc_no(self, doc) -> str:
        return getattr(doc, self.doc_number)

    def pay_no(self, pay) -> str:
        return getattr(pay, self.pay_number)


# ---------------------------------------------------------------------------
# Bucketing
# ---------------------------------------------------------------------------
def bucket_for(days_overdue: int) -> str:
    if days_overdue <= 0:
        return "current"
    if days_overdue <= 30:
        return "days_1_30"
    if days_overdue <= 60:
        return "days_31_60"
    if days_overdue <= 90:
        return "days_61_90"
    if days_overdue <= 120:
        return "days_91_120"
    return "days_over_120"


def _bucket_filters(as_of: dt.date) -> dict[str, Q]:
    d = lambda n: as_of - dt.timedelta(days=n)  # noqa: E731
    return {
        "current": Q(due_date__gte=as_of),
        "days_1_30": Q(due_date__lte=d(1), due_date__gte=d(30)),
        "days_31_60": Q(due_date__lte=d(31), due_date__gte=d(60)),
        "days_61_90": Q(due_date__lte=d(61), due_date__gte=d(90)),
        "days_91_120": Q(due_date__lte=d(91), due_date__gte=d(120)),
        "days_over_120": Q(due_date__lt=d(120)),
    }


# ---------------------------------------------------------------------------
# Payment status maintenance (derived values only)
# ---------------------------------------------------------------------------
def _active_alloc_sum(cfg: AccountConfig, **filters) -> Decimal:
    return cfg.allocation_model.objects.filter(is_active=True, **filters).aggregate(s=Coalesce(Sum("amount"), MZERO))["s"]


def refresh_document(cfg: AccountConfig, doc) -> None:
    paid = _active_alloc_sum(cfg, **{f"{cfg.alloc_doc}_id": doc.pk})
    doc.amount_paid = money(paid)
    doc.payment_status = payment_status_for(doc.total_amount, doc.amount_paid)
    doc.save(update_fields=["amount_paid", "payment_status", "updated_at"])


def refresh_payment(cfg: AccountConfig, payment) -> None:
    payment.allocated_amount = money(_active_alloc_sum(cfg, **{f"{cfg.alloc_pay}_id": payment.pk}))
    payment.save(update_fields=["allocated_amount", "updated_at"])


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------
def _party(cfg: AccountConfig, party_id: int):
    from apps.parties.models import Party

    party = Party.objects.filter(pk=party_id, type=cfg.party_type).first()
    if party is None:
        raise BusinessError(f"{cfg.party_type.title()} not found.", code="PARTY_NOT_FOUND")
    if not party.is_active:
        raise BusinessError(f"{party.name} is inactive.", code="PARTY_INACTIVE")
    return party


@transaction.atomic
def create_payment(
    cfg: AccountConfig,
    *,
    party_id: int,
    date: dt.date,
    amount: Decimal,
    payment_method: str,
    reference_number: str = "",
    notes: str = "",
    allocations: list[dict] | None = None,
    auto_allocate: bool = False,
    user,
):
    party = _party(cfg, party_id)
    amount = money(amount)
    if amount <= 0:
        raise BusinessError("Amount must be greater than zero.", code="INVALID_AMOUNT")
    payment = cfg.payment_model.objects.create(
        **{
            cfg.pay_number: DocumentSequence.next_number(cfg.sequence_key),
            cfg.pay_party: party,
            "date": date,
            "amount": amount,
            "payment_method": payment_method,
            "reference_number": reference_number,
            "notes": notes,
            "created_by": user,
        }
    )
    record(AuditAction.CREATE, payment, after=snapshot(payment), user=user)
    if allocations:
        allocate(cfg, payment, allocations, user=user)
    elif auto_allocate:
        auto_allocate_payment(cfg, payment, user=user)
    payment.refresh_from_db()
    return payment


@transaction.atomic
def allocate(cfg: AccountConfig, payment, allocations: list[dict], *, user) -> list:
    """Allocate a payment across several invoices/bills.

    ``allocations`` is ``[{"document": <id>, "amount": Decimal}, ...]``.
    Payment and documents are row-locked; each allocation is validated
    against the document's open balance and the payment's unallocated amount.
    """
    payment = cfg.payment_model.objects.select_for_update().get(pk=payment.pk)
    if payment.status != DocumentStatus.ACTIVE:
        raise AllocationError(f"{cfg.pay_no(payment)} is cancelled and cannot be allocated.")
    requested: dict[int, Decimal] = defaultdict(Decimal)
    for item in allocations:
        amt = money(item["amount"])
        if amt <= 0:
            raise AllocationError("Allocation amounts must be greater than zero.")
        requested[int(item["document"])] += amt
    if not requested:
        raise AllocationError("Nothing to allocate.")
    total = money(sum(requested.values()))
    if total > payment.unallocated_amount:
        raise AllocationError(
            f"Allocation total {total} exceeds the unallocated amount {payment.unallocated_amount} of {cfg.pay_no(payment)}."
        )
    docs = {
        d.pk: d
        for d in cfg.document_model.objects.select_for_update().filter(pk__in=sorted(requested)).order_by("pk")
    }
    party_id = getattr(payment, f"{cfg.pay_party}_id")
    created = []
    for doc_id in sorted(requested):
        doc = docs.get(doc_id)
        amt = requested[doc_id]
        if doc is None or getattr(doc, f"{cfg.doc_party}_id") != party_id:
            raise AllocationError(f"{cfg.doc_label} #{doc_id} does not belong to this {cfg.party_type.lower()}.")
        if doc.status != DocumentStatus.ACTIVE:
            raise AllocationError(f"{cfg.doc_no(doc)} is cancelled.")
        if amt > doc.balance_due:
            raise AllocationError(
                f"Allocation {amt} exceeds the outstanding balance {doc.balance_due} of {cfg.doc_no(doc)}."
            )
        alloc = cfg.allocation_model.objects.create(
            **{cfg.alloc_pay: payment, cfg.alloc_doc: doc},
            amount=amt,
            date=max(payment.date, doc.date),
            created_by=user,
        )
        refresh_document(cfg, doc)
        created.append(alloc)
    refresh_payment(cfg, payment)
    record(
        AuditAction.ALLOCATE,
        payment,
        after={"allocations": [{"document": cfg.doc_no(docs[getattr(a, f"{cfg.alloc_doc}_id")]), "amount": a.amount} for a in created]},
        user=user,
    )
    return created


def open_documents(cfg: AccountConfig, party_id: int):
    return (
        cfg.docs(party_id)
        .filter(status=DocumentStatus.ACTIVE, amount_paid__lt=F("total_amount"))
        .order_by("due_date", "date", "id")
    )


@transaction.atomic
def auto_allocate_payment(cfg: AccountConfig, payment, *, user) -> list:
    """Allocate the unallocated part of a payment to the oldest-due documents first."""
    payment = cfg.payment_model.objects.select_for_update().get(pk=payment.pk)
    remaining = payment.unallocated_amount
    plan = []
    for doc in open_documents(cfg, getattr(payment, f"{cfg.pay_party}_id")):
        if remaining <= 0:
            break
        amt = min(remaining, doc.balance_due)
        if amt > 0:
            plan.append({"document": doc.pk, "amount": amt})
            remaining -= amt
    return allocate(cfg, payment, plan, user=user) if plan else []


def _void(cfg: AccountConfig, alloc, *, reason: str, user) -> None:
    alloc.is_active = False
    alloc.voided_at = timezone.now()
    alloc.voided_by = user
    alloc.void_reason = reason[:255]
    alloc.save(update_fields=["is_active", "voided_at", "voided_by", "void_reason"])


@transaction.atomic
def unallocate(cfg: AccountConfig, allocation_id: int, *, reason: str, user):
    alloc = cfg.allocation_model.objects.select_for_update().filter(pk=allocation_id, is_active=True).first()
    if alloc is None:
        raise AllocationError("Allocation not found or already removed.")
    payment = cfg.payment_model.objects.select_for_update().get(pk=getattr(alloc, f"{cfg.alloc_pay}_id"))
    doc = cfg.document_model.objects.select_for_update().get(pk=getattr(alloc, f"{cfg.alloc_doc}_id"))
    _void(cfg, alloc, reason=reason or "Unallocated", user=user)
    refresh_document(cfg, doc)
    refresh_payment(cfg, payment)
    record(AuditAction.UPDATE, payment, before={"allocation": alloc.pk, "document": cfg.doc_no(doc), "amount": alloc.amount},
           after={"unallocated": True, "reason": reason}, user=user)
    return payment


def release_document_allocations(cfg: AccountConfig, doc, *, user) -> None:
    """Called when an invoice/bill is cancelled: its payments become unallocated."""
    allocs = list(cfg.allocation_model.objects.select_for_update().filter(**{f"{cfg.alloc_doc}_id": doc.pk, "is_active": True}))
    payment_ids = sorted({getattr(a, f"{cfg.alloc_pay}_id") for a in allocs})
    payments = list(cfg.payment_model.objects.select_for_update().filter(pk__in=payment_ids).order_by("pk"))
    for alloc in allocs:
        _void(cfg, alloc, reason=f"{cfg.doc_no(doc)} cancelled", user=user)
    for payment in payments:
        refresh_payment(cfg, payment)
    refresh_document(cfg, doc)


@transaction.atomic
def cancel_payment(cfg: AccountConfig, payment, *, reason: str, user):
    payment = cfg.payment_model.objects.select_for_update().get(pk=payment.pk)
    if payment.status == DocumentStatus.CANCELLED:
        raise BusinessError(f"{cfg.pay_no(payment)} is already cancelled.", code="ALREADY_CANCELLED")
    if not reason.strip():
        raise BusinessError("A cancellation reason is required.", code="REASON_REQUIRED")
    before = snapshot(payment)
    allocs = list(cfg.allocation_model.objects.select_for_update().filter(**{f"{cfg.alloc_pay}_id": payment.pk, "is_active": True}))
    doc_ids = sorted({getattr(a, f"{cfg.alloc_doc}_id") for a in allocs})
    docs = list(cfg.document_model.objects.select_for_update().filter(pk__in=doc_ids).order_by("pk"))
    for alloc in allocs:
        _void(cfg, alloc, reason=f"{cfg.pay_no(payment)} cancelled", user=user)
    for doc in docs:
        refresh_document(cfg, doc)
    refresh_payment(cfg, payment)
    payment.status = DocumentStatus.CANCELLED
    payment.cancelled_at = timezone.now()
    payment.cancelled_by = user
    payment.cancel_reason = reason.strip()
    payment.save()
    record(AuditAction.CANCEL, payment, before=before, after=snapshot(payment), user=user)
    return payment


def pay_on_creation(cfg: AccountConfig, doc, *, payment: dict, user):
    """Record a payment made together with the invoice/bill and allocate it."""
    amount = doc.total_amount if payment.get("pay_in_full") else money(payment["amount"])
    if amount <= 0:
        return None
    if amount > doc.total_amount:
        raise BusinessError(
            f"Amount paid ({amount}) cannot exceed the document total ({doc.total_amount}).", code="INVALID_AMOUNT"
        )
    return create_payment(
        cfg,
        party_id=getattr(doc, f"{cfg.doc_party}_id"),
        date=doc.date,
        amount=amount,
        payment_method=payment.get("payment_method", "CASH"),
        reference_number=payment.get("reference_number", ""),
        notes=f"Paid against {cfg.doc_no(doc)}",
        allocations=[{"document": doc.pk, "amount": amount}],
        user=user,
    )


# ---------------------------------------------------------------------------
# Historical (as-of) queries
# ---------------------------------------------------------------------------
def _alloc_active_at(as_of: dt.date) -> Q:
    return Q(date__lte=as_of) & (Q(is_active=True) | Q(voided_at__date__gt=as_of))


def docs_as_of(cfg: AccountConfig, as_of: dt.date, party_id=None):
    """Documents that existed (and were not yet cancelled) at ``as_of``,
    annotated with ``outstanding_as_of``."""
    allocated = (
        cfg.allocation_model.objects.filter(**{cfg.alloc_doc: OuterRef("pk")})
        .filter(_alloc_active_at(as_of))
        .order_by()
        .values(f"{cfg.alloc_doc}_id")
        .annotate(s=Sum("amount"))
        .values("s")
    )
    return (
        cfg.docs(party_id)
        .filter(date__lte=as_of)
        .exclude(status=DocumentStatus.CANCELLED, cancelled_at__date__lte=as_of)
        .annotate(allocated_as_of=Coalesce(Subquery(allocated, output_field=MONEY), MZERO))
        .annotate(outstanding_as_of=F("total_amount") - F("allocated_as_of"))
    )


def payments_as_of(cfg: AccountConfig, as_of: dt.date, party_id=None):
    allocated = (
        cfg.allocation_model.objects.filter(**{cfg.alloc_pay: OuterRef("pk")})
        .filter(_alloc_active_at(as_of))
        .order_by()
        .values(f"{cfg.alloc_pay}_id")
        .annotate(s=Sum("amount"))
        .values("s")
    )
    return (
        cfg.payments(party_id)
        .filter(date__lte=as_of)
        .exclude(status=DocumentStatus.CANCELLED, cancelled_at__date__lte=as_of)
        .annotate(allocated_as_of=Coalesce(Subquery(allocated, output_field=MONEY), MZERO))
        .annotate(unallocated_as_of=F("amount") - F("allocated_as_of"))
    )


def balance_as_of(cfg: AccountConfig, as_of: dt.date, party_id=None) -> Decimal:
    """Documents minus payments recognised up to ``as_of`` (cancellations reverse on their date)."""
    d = cfg.docs(party_id).aggregate(
        issued=Coalesce(Sum("total_amount", filter=Q(date__lte=as_of)), MZERO),
        reversed=Coalesce(Sum("total_amount", filter=Q(status=DocumentStatus.CANCELLED, cancelled_at__date__lte=as_of, date__lte=as_of)), MZERO),
    )
    p = cfg.payments(party_id).aggregate(
        paid=Coalesce(Sum("amount", filter=Q(date__lte=as_of)), MZERO),
        reversed=Coalesce(Sum("amount", filter=Q(status=DocumentStatus.CANCELLED, cancelled_at__date__lte=as_of, date__lte=as_of)), MZERO),
    )
    return money((d["issued"] - d["reversed"]) - (p["paid"] - p["reversed"]))


# ---------------------------------------------------------------------------
# Aging
# ---------------------------------------------------------------------------
def aging_summary(cfg: AccountConfig, as_of: dt.date, *, party_id=None, search=None) -> dict:
    """Per-party aging buckets computed from due dates at ``as_of``.

    Only the outstanding part of each (partially paid) document is aged.
    Unallocated payments/advances are reported separately and never counted
    as overdue balances.
    """
    from apps.parties.models import Party

    buckets = _bucket_filters(as_of)
    docs = docs_as_of(cfg, as_of, party_id).filter(outstanding_as_of__gt=0)
    party_field = f"{cfg.doc_party}_id"
    by_party = {
        row[party_field]: row
        for row in docs.values(party_field).annotate(
            **{name: Coalesce(Sum("outstanding_as_of", filter=q), MZERO) for name, q in buckets.items()},
            total=Coalesce(Sum("outstanding_as_of"), MZERO),
            document_count=Count("id"),
        ).order_by()
    }
    pay_field = f"{cfg.pay_party}_id"
    unallocated = {
        row[pay_field]: row["unallocated"]
        for row in payments_as_of(cfg, as_of, party_id)
        .filter(unallocated_as_of__gt=0)
        .values(pay_field)
        .annotate(unallocated=Sum("unallocated_as_of"))
        .order_by()
    }
    party_ids = set(by_party) | set(unallocated)
    parties = Party.objects.filter(pk__in=party_ids)
    if search:
        parties = parties.filter(Q(name__icontains=search) | Q(phone__icontains=search) | Q(pan_vat_no__icontains=search))
    rows = []
    totals = {name: Decimal("0.00") for name, _ in AGING_BUCKETS} | {"total": Decimal("0.00"), "unallocated": Decimal("0.00"), "net_balance": Decimal("0.00")}
    for party in parties.order_by("name"):
        base = by_party.get(party.pk, {})
        row = {
            "party_id": party.pk,
            "party_name": party.name,
            "phone": party.phone,
            "pan_vat_no": party.pan_vat_no,
            "credit_limit": party.credit_limit,
            **{name: money(base.get(name, 0)) for name, _ in AGING_BUCKETS},
            "total": money(base.get("total", 0)),
            "document_count": base.get("document_count", 0),
            "unallocated": money(unallocated.get(party.pk, 0)),
        }
        row["net_balance"] = money(row["total"] - row["unallocated"])
        for key in totals:
            totals[key] += row[key]
        rows.append(row)
    return {
        "as_of": as_of,
        "buckets": [{"key": k, "label": label} for k, label in AGING_BUCKETS],
        "rows": rows,
        "totals": {k: money(v) for k, v in totals.items()},
    }


def party_aging_detail(cfg: AccountConfig, party_id: int, as_of: dt.date) -> dict:
    docs = docs_as_of(cfg, as_of, party_id).filter(outstanding_as_of__gt=0).order_by("due_date", "date", "id")
    documents = []
    totals = {name: Decimal("0.00") for name, _ in AGING_BUCKETS}
    for doc in docs:
        days = (as_of - doc.due_date).days
        bucket = bucket_for(days)
        totals[bucket] += doc.outstanding_as_of
        documents.append(
            {
                "id": doc.pk,
                "number": cfg.doc_no(doc),
                "date": doc.date,
                "due_date": doc.due_date,
                "total_amount": doc.total_amount,
                "paid_as_of": money(doc.allocated_as_of),
                "outstanding": money(doc.outstanding_as_of),
                "days_overdue": max(days, 0),
                "bucket": bucket,
            }
        )
    payments = [
        {
            "id": p.pk,
            "number": cfg.pay_no(p),
            "date": p.date,
            "amount": p.amount,
            "unallocated": money(p.unallocated_as_of),
        }
        for p in payments_as_of(cfg, as_of, party_id).filter(unallocated_as_of__gt=0).order_by("date", "id")
    ]
    total = money(sum(totals.values(), Decimal("0")))
    unalloc = money(sum((p["unallocated"] for p in payments), Decimal("0")))
    return {
        "as_of": as_of,
        "buckets": [{"key": k, "label": label} for k, label in AGING_BUCKETS],
        "totals": {k: money(v) for k, v in totals.items()} | {"total": total, "unallocated": unalloc, "net_balance": money(total - unalloc)},
        "documents": documents,
        "unallocated_payments": payments,
    }


# ---------------------------------------------------------------------------
# Ledger & statements
# ---------------------------------------------------------------------------
def ledger(cfg: AccountConfig, party_id: int, *, start: dt.date | None = None, end: dt.date | None = None) -> dict:
    """Party ledger derived from invoices/bills, payments and their cancellations."""
    end = end or timezone.localdate()
    opening = balance_as_of(cfg, start - dt.timedelta(days=1), party_id) if start else Decimal("0.00")
    entries = []

    def in_range(field):
        q = Q(**{f"{field}__lte": end})
        return q & Q(**{f"{field}__gte": start}) if start else q

    for doc in cfg.docs(party_id).filter(in_range("date")):
        entries.append(_entry(cfg, doc, is_document=True, reversal=False))
    for doc in cfg.docs(party_id).filter(Q(status=DocumentStatus.CANCELLED) & in_range("cancelled_at__date")):
        entries.append(_entry(cfg, doc, is_document=True, reversal=True))
    for pay in cfg.payments(party_id).filter(in_range("date")):
        entries.append(_entry(cfg, pay, is_document=False, reversal=False))
    for pay in cfg.payments(party_id).filter(Q(status=DocumentStatus.CANCELLED) & in_range("cancelled_at__date")):
        entries.append(_entry(cfg, pay, is_document=False, reversal=True))

    entries.sort(key=lambda e: (e["date"], e["_order"]))
    balance = opening
    total_debit = total_credit = Decimal("0.00")
    for e in entries:
        e.pop("_order")
        total_debit += e["debit"]
        total_credit += e["credit"]
        balance += e["_effect"]
        e.pop("_effect")
        e["balance"] = money(balance)
    return {
        "opening_balance": money(opening),
        "closing_balance": money(balance),
        "total_debit": money(total_debit),
        "total_credit": money(total_credit),
        "start_date": start,
        "end_date": end,
        "entries": entries,
    }


def _entry(cfg: AccountConfig, obj, *, is_document: bool, reversal: bool) -> dict:
    amount = obj.total_amount if is_document else obj.amount
    # Effect on "amount owed": documents increase it, payments decrease it.
    effect = amount if is_document else -amount
    if reversal:
        effect = -effect
    increases_debit = (effect > 0) == cfg.document_is_debit
    label = cfg.doc_label if is_document else cfg.pay_label
    number = cfg.doc_no(obj) if is_document else cfg.pay_no(obj)
    date = timezone.localtime(obj.cancelled_at).date() if reversal else obj.date
    if is_document:
        description = f"{label} {number}" + (f" cancelled: {obj.cancel_reason}" if reversal else "")
    else:
        method = obj.get_payment_method_display()
        ref = f" ({obj.reference_number})" if obj.reference_number else ""
        description = f"{label} - {method}{ref}" + (f" cancelled: {obj.cancel_reason}" if reversal else "")
    order_ts = obj.cancelled_at if reversal else obj.created_at
    return {
        "date": date,
        "reference": number,
        "reference_id": obj.pk,
        "reference_type": ("document" if is_document else "payment"),
        "transaction_type": f"{label} Cancellation" if reversal else label,
        "description": description,
        "debit": money(abs(effect)) if increases_debit else Decimal("0.00"),
        "credit": Decimal("0.00") if increases_debit else money(abs(effect)),
        "due_date": obj.due_date if is_document and not reversal else None,
        "payment_status": obj.payment_status if is_document and not reversal else None,
        "status": obj.status,
        "_effect": effect,
        "_order": order_ts.timestamp() if order_ts else 0,
    }


# ---------------------------------------------------------------------------
# Party profile summary
# ---------------------------------------------------------------------------
def party_summary(cfg: AccountConfig, party) -> dict:
    today = timezone.localdate()
    active_docs = cfg.docs(party.pk).filter(status=DocumentStatus.ACTIVE)
    d = active_docs.aggregate(
        total=Coalesce(Sum("total_amount"), MZERO),
        paid=Coalesce(Sum("amount_paid"), MZERO),
        overdue=Coalesce(Sum(F("total_amount") - F("amount_paid"), filter=Q(due_date__lt=today)), MZERO),
        current=Coalesce(Sum(F("total_amount") - F("amount_paid"), filter=Q(due_date__gte=today)), MZERO),
        count=Count("id"),
        last=Max("date"),
    )
    p = cfg.payments(party.pk).filter(status=DocumentStatus.ACTIVE).aggregate(
        total=Coalesce(Sum("amount"), MZERO),
        unallocated=Coalesce(Sum(F("amount") - F("allocated_amount")), MZERO),
        count=Count("id"),
        last=Max("date"),
    )
    outstanding = money(d["total"] - d["paid"])
    last_dates = [x for x in (d["last"], p["last"]) if x]
    net = money(outstanding - p["unallocated"])
    return {
        "party_id": party.pk,
        "total_documents": money(d["total"]),
        "document_count": d["count"],
        "total_payments": money(p["total"]),
        "payment_count": p["count"],
        "outstanding": outstanding,
        "overdue": money(d["overdue"]),
        "current": money(d["current"]),
        "unallocated": money(p["unallocated"]),
        "net_balance": net,
        "last_transaction_date": max(last_dates) if last_dates else None,
        "credit_limit": party.credit_limit,
        "available_credit": money(party.credit_limit - net) if party.credit_limit else None,
    }


def totals_overview(cfg: AccountConfig) -> dict:
    """Company-wide outstanding and overdue totals (dashboard)."""
    today = timezone.localdate()
    agg = cfg.docs().filter(status=DocumentStatus.ACTIVE).aggregate(
        outstanding=Coalesce(Sum(F("total_amount") - F("amount_paid")), MZERO),
        overdue=Coalesce(Sum(F("total_amount") - F("amount_paid"), filter=Q(due_date__lt=today)), MZERO),
    )
    unalloc = cfg.payments().filter(status=DocumentStatus.ACTIVE).aggregate(
        s=Coalesce(Sum(F("amount") - F("allocated_amount")), MZERO)
    )["s"]
    return {"outstanding": money(agg["outstanding"]), "overdue": money(agg["overdue"]), "unallocated": money(unalloc)}

