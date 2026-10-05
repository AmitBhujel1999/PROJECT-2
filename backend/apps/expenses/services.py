"""Expense services: categories, create_expense(), cancel_expense().

Every amount is calculated here with Decimal; the browser only supplies the
net amount and the tax rate.
"""

from __future__ import annotations

from django.db import transaction
from django.utils import timezone

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot
from apps.common.exceptions import BusinessError
from apps.common.models import DocumentSequence, DocumentStatus
from apps.common.money import HUNDRED, ZERO, money, rate
from apps.parties.models import Party, PartyType

from .models import Expense, ExpenseCategory


def calculate_expense(amount, tax_rate) -> dict:
    amount = money(amount)
    tax_rate = rate(tax_rate or ZERO)
    tax_amount = money(amount * tax_rate / HUNDRED)
    return {"amount": amount, "tax_rate": tax_rate, "tax_amount": tax_amount, "total_amount": money(amount + tax_amount)}


# ---------------------------------------------------------------------------
# Categories
# ---------------------------------------------------------------------------
DEFAULT_CATEGORIES = [
    ("Rent", "Office, shop and warehouse rent"),
    ("Salaries & Wages", "Staff salaries, wages and allowances"),
    ("Utilities", "Electricity, water, internet and phone"),
    ("Transport & Fuel", "Freight, delivery, vehicle fuel and travel"),
    ("Office Supplies", "Stationery, printing and small office items"),
    ("Repairs & Maintenance", "Repairs to premises, equipment and vehicles"),
    ("Marketing & Advertising", "Ads, promotions and signage"),
    ("Bank Charges", "Bank fees, commissions and service charges"),
    ("Professional Fees", "Audit, legal and consulting fees"),
    ("Taxes & Licenses", "Registration renewals, licenses and non-recoverable taxes"),
    ("Miscellaneous", "Expenses that fit no other category"),
]


def ensure_default_categories() -> dict[str, ExpenseCategory]:
    """Create any missing default categories (idempotent); returns all categories by name."""
    for name, description in DEFAULT_CATEGORIES:
        if not ExpenseCategory.objects.filter(name__iexact=name).exists():
            ExpenseCategory.objects.create(name=name, description=description)
    return {c.name: c for c in ExpenseCategory.objects.all()}


def _ensure_unique_name(name: str, exclude_pk=None) -> None:
    qs = ExpenseCategory.objects.filter(name__iexact=name.strip())
    if exclude_pk:
        qs = qs.exclude(pk=exclude_pk)
    if qs.exists():
        raise BusinessError(f"An expense category named {name.strip()} already exists.", code="DUPLICATE_CATEGORY")


@transaction.atomic
def create_category(*, data: dict, user) -> ExpenseCategory:
    _ensure_unique_name(data["name"])
    category = ExpenseCategory.objects.create(**data)
    record(AuditAction.CREATE, category, after=snapshot(category), user=user)
    return category


@transaction.atomic
def update_category(category: ExpenseCategory, *, data: dict, user) -> ExpenseCategory:
    category = ExpenseCategory.objects.select_for_update().get(pk=category.pk)
    if "name" in data:
        _ensure_unique_name(data["name"], exclude_pk=category.pk)
    before = snapshot(category)
    for key, value in data.items():
        setattr(category, key, value)
    category.save()
    record(AuditAction.UPDATE, category, before=before, after=snapshot(category), user=user)
    return category


@transaction.atomic
def set_category_active(category: ExpenseCategory, *, active: bool, user) -> ExpenseCategory:
    before = snapshot(category)
    category.is_active = active
    category.save(update_fields=["is_active", "updated_at"])
    action = AuditAction.ACTIVATE if active else AuditAction.DEACTIVATE
    record(action, category, before=before, after=snapshot(category), user=user)
    return category


@transaction.atomic
def delete_category(category: ExpenseCategory, *, user) -> None:
    """Physically delete only categories that were never used."""
    if category.expenses.exists():
        raise BusinessError(
            "This category has expenses and cannot be deleted. Deactivate it instead.",
            code="CATEGORY_HAS_EXPENSES",
            status_code=409,
        )
    record(AuditAction.DELETE, category, before=snapshot(category), user=user)
    category.delete()


# ---------------------------------------------------------------------------
# Expenses
# ---------------------------------------------------------------------------
def _category(category_id: int) -> ExpenseCategory:
    category = ExpenseCategory.objects.filter(pk=category_id).first()
    if category is None:
        raise BusinessError("Expense category not found.", code="CATEGORY_NOT_FOUND")
    if not category.is_active:
        raise BusinessError(f"Expense category {category.name} is inactive.", code="CATEGORY_INACTIVE")
    return category


def _vendor(vendor_id: int | None) -> Party | None:
    if not vendor_id:
        return None
    vendor = Party.objects.filter(pk=vendor_id, type=PartyType.VENDOR).first()
    if vendor is None:
        raise BusinessError("Vendor not found.", code="VENDOR_NOT_FOUND")
    if not vendor.is_active:
        raise BusinessError(f"Vendor {vendor.name} is inactive.", code="PARTY_INACTIVE")
    return vendor


@transaction.atomic
def create_expense(*, data: dict, user) -> Expense:
    category = _category(data["category"])
    vendor = _vendor(data.get("vendor"))
    totals = calculate_expense(data["amount"], data.get("tax_rate"))
    expense = Expense.objects.create(
        expense_number=DocumentSequence.next_number(DocumentSequence.EXPENSE),
        date=data["date"],
        category=category,
        vendor=vendor,
        payee="" if vendor else data.get("payee", "").strip(),
        description=data["description"].strip(),
        payment_method=data.get("payment_method", "CASH"),
        reference_number=data.get("reference_number", ""),
        notes=data.get("notes", ""),
        created_by=user,
        **totals,
    )
    record(AuditAction.CREATE, expense, after=snapshot(expense), user=user)
    return expense


@transaction.atomic
def cancel_expense(expense: Expense, *, reason: str, user) -> Expense:
    expense = Expense.objects.select_for_update().get(pk=expense.pk)
    if expense.status == DocumentStatus.CANCELLED:
        raise BusinessError(f"{expense.expense_number} is already cancelled.", code="ALREADY_CANCELLED")
    if not reason.strip():
        raise BusinessError("A cancellation reason is required.", code="REASON_REQUIRED")
    before = snapshot(expense)
    expense.status = DocumentStatus.CANCELLED
    expense.cancelled_at = timezone.now()
    expense.cancelled_by = user
    expense.cancel_reason = reason.strip()
    expense.save()
    record(AuditAction.CANCEL, expense, before=before, after=snapshot(expense), user=user)
    return expense
