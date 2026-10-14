"""Opening Balances: every opening amount in the books on one screen.

Openings live on their own records (cash/bank accounts, chart-of-accounts
ledgers, customers, vendors and products); this module reads them together and
saves changes through each module's own service, so audit and stock rules apply.
"""

from __future__ import annotations

import datetime as dt
from decimal import Decimal, InvalidOperation

from django.db import transaction

from apps.common.exceptions import BusinessError
from apps.common.money import money
from apps.users.permissions import has_perm

D0 = Decimal("0")
SECTION_PERMS = {
    "cash": "cashbank.manage",
    "ledgers": "journal.manage_ledgers",
    "customers": "parties.manage",
    "vendors": "parties.manage",
    "stock": "products.manage",
}


def _side(signed: Decimal) -> dict:
    return {"debit": money(max(signed, D0)), "credit": money(max(-signed, D0))}


def _signed(amount: Decimal, side: str) -> Decimal:
    return amount if side == "DR" else -amount


def opening_balances(user) -> dict:
    from apps.cashbank.models import MoneyAccount
    from apps.journal.models import LedgerAccount
    from apps.parties.models import Party, PartyType
    from apps.products.models import Product

    from .statements import opening_stock_value

    cash = [
        {"id": a.pk, "name": a.name, "kind": a.get_kind_display(), "is_active": a.is_active,
         "opening_balance": money(a.opening_balance), "opening_side": "DR"}
        for a in MoneyAccount.objects.order_by("name")
    ]
    ledgers = [
        {"id": lg.pk, "name": lg.name, "group": lg.group, "group_display": lg.get_group_display(), "is_active": lg.is_active,
         "opening_balance": money(lg.opening_balance), "opening_side": lg.opening_side}
        for lg in LedgerAccount.objects.order_by("group", "name")
    ]

    def parties(kind):
        return [
            {"id": p.pk, "name": p.name, "phone": p.phone, "is_active": p.is_active,
             "opening_balance": money(p.opening_balance), "opening_side": p.opening_side}
            for p in Party.objects.filter(type=kind).order_by("name")
        ]

    customers, vendors = parties(PartyType.CUSTOMER), parties(PartyType.VENDOR)
    stock = [
        {"id": p.pk, "name": p.name, "sku_code": p.sku_code, "unit": p.get_unit_display(), "is_active": p.is_active,
         "opening_stock": p.opening_stock, "opening_rate": money(p.opening_rate), "opening_amount": p.opening_amount}
        for p in Product.objects.order_by("name")
    ]
    # Stock is valued from the posted opening movements (the cost they were entered at).
    stock_value = opening_stock_value(dt.date.max)

    def total(rows):
        return sum((_signed(r["opening_balance"], r["opening_side"]) for r in rows), D0)

    totals = {
        "cash": _side(total(cash)),
        "ledgers": _side(total(ledgers)),
        "customers": _side(total(customers)),
        "vendors": _side(total(vendors)),
        "stock": _side(stock_value),
    }
    debit = money(sum((t["debit"] for t in totals.values()), D0))
    credit = money(sum((t["credit"] for t in totals.values()), D0))
    return {
        "cash": cash,
        "ledgers": ledgers,
        "customers": customers,
        "vendors": vendors,
        "stock": stock,
        "totals": totals,
        "total_debit": debit,
        "total_credit": credit,
        # Shown on the capital side of the balance sheet; zero once capital is entered.
        "difference": _side(debit - credit),
        "can_edit": {k: has_perm(user, perm) for k, perm in SECTION_PERMS.items()},
    }


def _amount(value, label) -> Decimal:
    try:
        amount = money(Decimal(str(value)))
    except (InvalidOperation, TypeError, ValueError) as err:
        raise BusinessError(f"{label}: enter a valid amount.", code="INVALID_AMOUNT") from err
    if amount < 0:
        raise BusinessError(f"{label}: amount cannot be negative.", code="INVALID_AMOUNT")
    return amount


def _quantity(value, label) -> Decimal:
    try:
        q = Decimal(str(value)).quantize(Decimal("0.001"))
    except (InvalidOperation, TypeError, ValueError) as err:
        raise BusinessError(f"{label}: enter a valid quantity.", code="INVALID_QUANTITY") from err
    if q < 0:
        raise BusinessError(f"{label}: quantity cannot be negative.", code="INVALID_QUANTITY")
    return q


def _opening_side(value, label) -> str:
    if value not in ("DR", "CR"):
        raise BusinessError(f"{label}: side must be Dr or Cr.", code="INVALID_SIDE")
    return value


@transaction.atomic
def save_opening_balances(data: dict, *, user) -> int:
    """Apply changed openings. Each section needs its own permission. Returns rows changed."""
    from apps.cashbank.models import MoneyAccount
    from apps.cashbank.services import update_account
    from apps.journal.models import LedgerAccount
    from apps.journal.services import update_ledger
    from apps.parties.models import Party, PartyType
    from apps.parties.services import update_party
    from apps.products.models import Product
    from apps.products.services import update_product

    for section, perm in SECTION_PERMS.items():
        if data.get(section) and not has_perm(user, perm):
            raise BusinessError("You do not have permission to change these opening balances.", code="PERMISSION_DENIED", status_code=403)

    changed = 0
    for item in data.get("cash") or []:
        account = MoneyAccount.objects.filter(pk=item.get("id")).first()
        if not account:
            continue
        amount = _amount(item.get("opening_balance"), account.name)
        if amount != account.opening_balance:
            update_account(account, data={"opening_balance": amount}, user=user)
            changed += 1

    for item in data.get("ledgers") or []:
        ledger = LedgerAccount.objects.filter(pk=item.get("id")).first()
        if not ledger:
            continue
        amount = _amount(item.get("opening_balance"), ledger.name)
        side = _opening_side(item.get("opening_side", ledger.opening_side), ledger.name)
        if (amount, side) != (ledger.opening_balance, ledger.opening_side):
            update_ledger(ledger, data={"opening_balance": amount, "opening_side": side}, user=user)
            changed += 1

    for section, kind in (("customers", PartyType.CUSTOMER), ("vendors", PartyType.VENDOR)):
        for item in data.get(section) or []:
            party = Party.objects.filter(pk=item.get("id"), type=kind).first()
            if not party:
                continue
            amount = _amount(item.get("opening_balance"), party.name)
            side = _opening_side(item.get("opening_side", party.opening_side), party.name)
            if (amount, side) != (party.opening_balance, party.opening_side):
                update_party(party, data={"opening_balance": amount, "opening_side": side}, user=user)
                changed += 1

    for item in data.get("stock") or []:
        product = Product.objects.filter(pk=item.get("id")).first()
        if not product:
            continue
        quantity = _quantity(item.get("opening_stock", product.opening_stock), product.name)
        rate = _amount(item.get("opening_rate", product.opening_rate), product.name)
        if rate != product.opening_rate:
            if product.opening_stock > 0:
                # Posted opening stock keeps the cost it was entered at.
                raise BusinessError(
                    f"{product.name}: the opening rate can only be changed while the opening quantity is 0.",
                    code="OPENING_RATE_LOCKED",
                )
            product = update_product(product, data={"opening_rate": rate}, user=user)
            changed += 1
        if quantity != product.opening_stock:
            update_product(product, data={"opening_stock": quantity}, user=user)
            changed += 1
    return changed
