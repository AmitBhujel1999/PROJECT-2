"""Financial statements derived from the books: Profit & Loss, Balance Sheet, Cash Flow.

Nothing is posted twice: every figure is calculated from the entries the app
already keeps (sales, purchases, receipts, payments, expenses, contra, journal
vouchers, stock movements and opening balances).

Rules
- Cancelled documents count as if they never happened, everywhere (stock too),
  so the statements agree with each other on every date.
- Amounts are excluding VAT. VAT collected minus VAT paid is a liability
  (or an asset when negative).
- Closing stock is valued at weighted average cost: the average net cost of all
  units received (opening stock and purchases) up to the date.
- Opening balances (cash/bank, opening stock, chart-of-accounts ledgers) have no
  matching entry, so their total is shown as "Difference in opening balances"
  on the capital side, as Tally/Busy do.
"""

from __future__ import annotations

import datetime as dt
from collections import defaultdict
from decimal import Decimal

from django.db.models import Q, Sum

from apps.common.models import DocumentStatus
from apps.common.money import money

D0 = Decimal("0")
ACTIVE = DocumentStatus.ACTIVE


def _sum(qs, field: str) -> Decimal:
    return qs.aggregate(t=Sum(field))["t"] or D0


def _dated(qs, start: dt.date | None, end: dt.date, field: str = "date"):
    qs = qs.filter(**{f"{field}__lte": end})
    return qs.filter(**{f"{field}__gte": start}) if start else qs


# ---------------------------------------------------------------------------
# Stock at weighted average cost
# ---------------------------------------------------------------------------
def _live_movements():
    """Stock movements, leaving out everything that belongs to a cancelled sale or purchase."""
    from apps.inventory.models import StockMovement
    from apps.purchases.models import Purchase
    from apps.sales.models import Sale

    cancelled_sales = Sale.objects.exclude(status=ACTIVE).values("pk")
    cancelled_purchases = Purchase.objects.exclude(status=ACTIVE).values("pk")
    return StockMovement.objects.exclude(
        Q(reference_type="sale", reference_id__in=cancelled_sales)
        | Q(reference_type="purchase", reference_id__in=cancelled_purchases)
    )


def stock_value(as_of: dt.date) -> dict:
    """Closing stock value per product and in total at the end of ``as_of``."""
    from apps.inventory.models import MovementType
    from apps.products.models import Product

    moves = _live_movements().filter(date__lte=as_of)
    qty = {r["product_id"]: r["q"] for r in moves.values("product_id").annotate(q=Sum("quantity_in") - Sum("quantity_out")).order_by()}
    cost = defaultdict(lambda: [D0, D0])  # product -> [quantity received, cost of it]
    received = moves.filter(transaction_type__in=[MovementType.OPENING_STOCK, MovementType.PURCHASE])
    for m in received.values("product_id", "quantity_in", "quantity_out", "unit_cost"):
        net = m["quantity_in"] - m["quantity_out"]
        cost[m["product_id"]][0] += net
        cost[m["product_id"]][1] += net * m["unit_cost"]
    prices = dict(Product.objects.filter(pk__in=qty).values_list("pk", "purchase_price"))
    total = D0
    for pid, q in qty.items():
        if q <= 0:
            continue
        units, value = cost[pid]
        rate = value / units if units > 0 else prices.get(pid, D0)
        total += q * rate
    return {"value": money(total)}


def opening_stock_value(end: dt.date) -> Decimal:
    """Cost of all opening-stock movements up to ``end`` (goods brought in at the start)."""
    from apps.inventory.models import MovementType

    total = D0
    for m in _live_movements().filter(date__lte=end, transaction_type=MovementType.OPENING_STOCK).values(
        "quantity_in", "quantity_out", "unit_cost"
    ):
        total += (m["quantity_in"] - m["quantity_out"]) * m["unit_cost"]
    return money(total)


# ---------------------------------------------------------------------------
# Profit & Loss
# ---------------------------------------------------------------------------
def _ledger_movement(start, end, nature: str) -> list[dict]:
    """Journal amounts on income or expense ledgers in the period, by ledger."""
    from apps.journal.models import GROUP_NATURE, LedgerAccount
    from apps.journal.services import active_lines

    groups = [g for g, n in GROUP_NATURE.items() if n == nature]
    lines = _dated(active_lines().filter(ledger__group__in=groups), start, end, "voucher__date")
    sign = 1 if nature == "EXPENSE" else -1  # expenses are debits, income credits
    names = dict(LedgerAccount.objects.values_list("pk", "name"))
    group_of = dict(LedgerAccount.objects.values_list("pk", "group"))
    rows = []
    for r in lines.values("ledger_id").annotate(dr=Sum("debit"), cr=Sum("credit")).order_by():
        amount = money(sign * (r["dr"] - r["cr"]))
        if amount:
            rows.append({"id": r["ledger_id"], "name": names[r["ledger_id"]], "group": group_of[r["ledger_id"]], "amount": amount})
    return sorted(rows, key=lambda r: r["name"])


def _expense_categories(start, end) -> list[dict]:
    from apps.expenses.models import Expense, ExpenseCategory
    from apps.journal.services import expense_category_totals

    totals = defaultdict(lambda: D0)
    for r in _dated(Expense.objects.filter(status=ACTIVE), start, end).values("category_id").annotate(t=Sum("amount")).order_by():
        totals[r["category_id"]] += r["t"]
    for cid, amount in expense_category_totals(start=start, end=end).items():
        totals[cid] += amount
    names = dict(ExpenseCategory.objects.filter(pk__in=totals).values_list("pk", "name"))
    rows = [{"id": cid, "name": names.get(cid, ""), "amount": money(v)} for cid, v in totals.items() if v]
    return sorted(rows, key=lambda r: -r["amount"])


def profit_and_loss(start: dt.date | None, end: dt.date) -> dict:
    from apps.purchases.models import Purchase
    from apps.sales.models import Sale

    sales = money(_sum(_dated(Sale.objects.filter(status=ACTIVE), start, end), "taxable_amount"))
    purchases = money(_sum(_dated(Purchase.objects.filter(status=ACTIVE), start, end), "taxable_amount"))
    if start:
        opening = stock_value(start - dt.timedelta(days=1))["value"]
        # Opening stock entered (or corrected) during the period is goods received in the period.
        brought_in = money(opening_stock_value(end) - opening_stock_value(start - dt.timedelta(days=1)))
    else:
        opening, brought_in = money(D0), opening_stock_value(end)
    closing = stock_value(end)["value"]
    cogs = money(opening + brought_in + purchases - closing)

    income = _ledger_movement(start, end, "INCOME")
    expense_ledgers = _ledger_movement(start, end, "EXPENSE")
    direct_income = [r for r in income if r["group"] == "DIRECT_INCOME"]
    indirect_income = [r for r in income if r["group"] != "DIRECT_INCOME"]
    direct_expenses = [r for r in expense_ledgers if r["group"] == "DIRECT_EXPENSES"]
    indirect_ledgers = [r for r in expense_ledgers if r["group"] != "DIRECT_EXPENSES"]
    categories = _expense_categories(start, end)

    total = lambda rows: money(sum((r["amount"] for r in rows), D0))  # noqa: E731
    gross = money(sales + total(direct_income) - cogs - total(direct_expenses))
    indirect_expense_total = money(total(categories) + total(indirect_ledgers))
    net = money(gross + total(indirect_income) - indirect_expense_total)
    return {
        "start_date": start,
        "end_date": end,
        "sales": sales,
        "direct_income": direct_income,
        "opening_stock": opening,
        "opening_stock_added": brought_in,
        "purchases": purchases,
        "closing_stock": closing,
        "cost_of_goods_sold": cogs,
        "direct_expenses": direct_expenses,
        "gross_profit": gross,
        "indirect_income": indirect_income,
        "expense_categories": categories,
        "indirect_expenses": indirect_ledgers,
        "total_indirect_expenses": indirect_expense_total,
        "net_profit": net,
    }


# ---------------------------------------------------------------------------
# Balance Sheet
# ---------------------------------------------------------------------------
def party_owed(as_of: dt.date) -> dict[int, Decimal]:
    """What each party owes us (customers) or we owe them (vendors), counting only
    active documents and payments, journal lines and opening balances."""
    from apps.journal.services import active_lines
    from apps.parties.models import PartyType
    from apps.payables.models import VendorPayment
    from apps.purchases.models import Purchase
    from apps.receivables.models import CustomerReceipt
    from apps.sales.models import Sale

    owed = defaultdict(lambda: D0)  # customers: they owe us; vendors: we owe them
    for model, party, field, sign in [
        (Sale, "customer_id", "total_amount", 1),
        (CustomerReceipt, "customer_id", "amount", -1),
        (Purchase, "vendor_id", "total_amount", 1),
        (VendorPayment, "vendor_id", "amount", -1),
    ]:
        for r in model.objects.filter(status=ACTIVE, date__lte=as_of).values(party).annotate(t=Sum(field)).order_by():
            owed[r[party]] += sign * r["t"]
    lines = active_lines().filter(voucher__date__lte=as_of, party__isnull=False)
    for r in lines.values("party_id", "party__type").annotate(dr=Sum("debit"), cr=Sum("credit")).order_by():
        net = r["dr"] - r["cr"]
        owed[r["party_id"]] += net if r["party__type"] == PartyType.CUSTOMER else -net
    from apps.parties.models import Party

    for p in Party.objects.filter(opening_balance__gt=0).only("pk", "type", "opening_balance", "opening_side"):
        owed[p.pk] += p.signed_opening if p.type == PartyType.CUSTOMER else -p.signed_opening
    return {k: money(v) for k, v in owed.items() if v}


def _party_balances(as_of: dt.date) -> dict:
    from apps.parties.models import Party, PartyType

    owed = party_owed(as_of)
    types = dict(Party.objects.filter(pk__in=owed).values_list("pk", "type"))
    out = {"debtors": D0, "customer_advances": D0, "creditors": D0, "supplier_advances": D0}
    for pid, amount in owed.items():
        if types.get(pid) == PartyType.CUSTOMER:
            out["debtors" if amount > 0 else "customer_advances"] += abs(amount)
        else:
            out["creditors" if amount > 0 else "supplier_advances"] += abs(amount)
    return {k: money(v) for k, v in out.items()}


def _vat_balance(as_of: dt.date) -> Decimal:
    """VAT collected on sales minus VAT paid on purchases and expenses (positive = payable)."""
    from apps.expenses.models import Expense
    from apps.purchases.models import Purchase
    from apps.sales.models import Sale

    out = _sum(Sale.objects.filter(status=ACTIVE, date__lte=as_of), "tax_amount")
    paid = _sum(Purchase.objects.filter(status=ACTIVE, date__lte=as_of), "tax_amount")
    paid += _sum(Expense.objects.filter(status=ACTIVE, date__lte=as_of), "tax_amount")
    return money(out - paid)


def opening_difference() -> Decimal:
    """Signed (Dr +) total of every opening balance; it has no matching entry."""
    from apps.cashbank.models import MoneyAccount
    from apps.journal.models import LedgerAccount
    from apps.journal.services import signed_opening
    from apps.parties.models import Party

    return money(
        sum((a.opening_balance for a in MoneyAccount.objects.all()), D0)
        + sum((signed_opening(lg) for lg in LedgerAccount.objects.all()), D0)
        + sum((p.signed_opening for p in Party.objects.filter(opening_balance__gt=0)), D0)
    )


def balance_sheet(as_of: dt.date) -> dict:
    from apps.cashbank.models import MoneyAccount
    from apps.cashbank.services import balance as account_balance
    from apps.journal.models import GROUP_NATURE, LedgerAccount, Nature
    from apps.journal.services import ledger_balances

    cash = [
        {"id": a.pk, "name": a.name, "amount": account_balance(a, as_of)}
        for a in MoneyAccount.objects.order_by("name")
    ]
    cash = [c for c in cash if c["amount"]]
    parties = _party_balances(as_of)
    vat = _vat_balance(as_of)
    stock = stock_value(as_of)["value"]
    ledgers = ledger_balances(as_of)

    by_group = defaultdict(list)
    for lg in LedgerAccount.objects.order_by("name"):
        nature = GROUP_NATURE[lg.group]
        if nature not in (Nature.ASSET, Nature.LIABILITY):
            continue
        amount = ledgers.get(lg.pk, D0)
        if not amount:
            continue
        # Show assets as debit-positive and liabilities as credit-positive.
        by_group[lg.group].append({"id": lg.pk, "name": lg.name, "amount": money(amount if nature == Nature.ASSET else -amount)})

    profit = profit_and_loss(None, as_of)["net_profit"]
    opening_diff = money(opening_difference() + opening_stock_value(as_of))

    def group(key, label, rows):
        return {"key": key, "label": label, "rows": rows, "total": money(sum((r["amount"] for r in rows), D0))}

    from apps.journal.models import AccountGroup

    liability_groups = [
        group("CAPITAL", "Capital Account", [
            *by_group[AccountGroup.CAPITAL],
            {"id": None, "name": "Profit & Loss A/c", "amount": profit, "link": "profit-loss"},
            {"id": None, "name": "Difference in opening balances", "amount": opening_diff},
        ]),
        group("RESERVES", "Reserves & Surplus", by_group[AccountGroup.RESERVES]),
        group("LOANS", "Loans (Liability)", by_group[AccountGroup.LOANS]),
        group("CURRENT_LIABILITIES", "Current Liabilities", [
            *([{"id": None, "name": "Sundry Creditors", "amount": parties["creditors"], "link": "payables"}] if parties["creditors"] else []),
            *([{"id": None, "name": "Advances from customers", "amount": parties["customer_advances"], "link": "receivables"}]
              if parties["customer_advances"] else []),
            *by_group[AccountGroup.CURRENT_LIABILITIES],
        ]),
        group("DUTIES_TAXES", "Duties & Taxes", [
            *([{"id": None, "name": "VAT payable", "amount": vat}] if vat > 0 else []),
            *by_group[AccountGroup.DUTIES_TAXES],
        ]),
    ]
    asset_groups = [
        group("FIXED_ASSETS", "Fixed Assets", by_group[AccountGroup.FIXED_ASSETS]),
        group("INVESTMENTS", "Investments", by_group[AccountGroup.INVESTMENTS]),
        group("CURRENT_ASSETS", "Current Assets", [
            *([{"id": None, "name": "Closing stock", "amount": stock, "link": "stock"}] if stock else []),
            *([{"id": None, "name": "Sundry Debtors", "amount": parties["debtors"], "link": "receivables"}] if parties["debtors"] else []),
            *([{"id": None, "name": "Advances to suppliers", "amount": parties["supplier_advances"], "link": "payables"}]
              if parties["supplier_advances"] else []),
            *([{"id": None, "name": "VAT receivable", "amount": -vat}] if vat < 0 else []),
            *by_group[AccountGroup.CURRENT_ASSETS],
        ]),
        group("CASH_BANK", "Cash & Bank", [{**c, "link": "cash-bank"} for c in cash]),
        group("LOANS_ADVANCES", "Loans & Advances (Asset)", by_group[AccountGroup.LOANS_ADVANCES]),
    ]
    liability_groups = [g for g in liability_groups if g["rows"]]
    asset_groups = [g for g in asset_groups if g["rows"]]
    total_liabilities = money(sum((g["total"] for g in liability_groups), D0))
    total_assets = money(sum((g["total"] for g in asset_groups), D0))
    return {
        "as_of": as_of,
        "liabilities": liability_groups,
        "assets": asset_groups,
        "total_liabilities": total_liabilities,
        "total_assets": total_assets,
        "difference": money(total_assets - total_liabilities),
        "net_profit": profit,
    }


# ---------------------------------------------------------------------------
# Cash Flow (direct method, from the cash/bank books)
# ---------------------------------------------------------------------------
INVESTING = {"FIXED_ASSETS", "INVESTMENTS"}
FINANCING = {"CAPITAL", "RESERVES", "LOANS"}


def cash_flow(start: dt.date, end: dt.date) -> dict:
    from apps.cashbank.models import MoneyAccount
    from apps.cashbank.services import balance as account_balance
    from apps.expenses.models import Expense
    from apps.journal.models import JournalLine
    from apps.journal.services import active_lines
    from apps.payables.models import VendorPayment
    from apps.receivables.models import CustomerReceipt

    accounts = list(MoneyAccount.objects.all())
    opening = money(sum((account_balance(a, start - dt.timedelta(days=1)) for a in accounts), D0))
    closing = money(sum((account_balance(a, end) for a in accounts), D0))

    receipts = money(_sum(_dated(CustomerReceipt.objects.filter(status=ACTIVE), start, end), "amount"))
    payments = money(_sum(_dated(VendorPayment.objects.filter(status=ACTIVE), start, end), "amount"))
    expenses = money(_sum(_dated(Expense.objects.filter(status=ACTIVE), start, end), "total_amount"))

    # Journal lines on cash/bank, classified by what the rest of the voucher touched.
    sections = {"operating": defaultdict(lambda: D0), "investing": defaultdict(lambda: D0), "financing": defaultdict(lambda: D0)}
    cash_lines = _dated(active_lines().filter(money_account__isnull=False), start, end, "voucher__date")
    voucher_ids = set(cash_lines.values_list("voucher_id", flat=True))
    groups_of = defaultdict(set)
    names = defaultdict(set)
    others = JournalLine.objects.filter(voucher_id__in=voucher_ids, money_account__isnull=True, ledger__isnull=False)
    for voucher_id, group, name in others.values_list("voucher_id", "ledger__group", "ledger__name"):
        groups_of[voucher_id].add(group)
        names[voucher_id].add(name)
    for line in cash_lines:
        groups = groups_of[line.voucher_id]
        section = "investing" if groups & INVESTING else "financing" if groups & FINANCING else "operating"
        label = ", ".join(sorted(names[line.voucher_id])) or "Journal adjustments"
        sections[section][label] += line.debit - line.credit

    def rows(section):
        return [{"name": k, "amount": money(v)} for k, v in sorted(sections[section].items()) if v]

    operating = [
        {"name": "Received from customers", "amount": receipts, "link": "receipts"},
        {"name": "Paid to suppliers", "amount": -payments, "link": "payments"},
        {"name": "Expenses paid", "amount": -expenses, "link": "expenses"},
        *rows("operating"),
    ]
    operating = [r for r in operating if r["amount"]]
    investing, financing = rows("investing"), rows("financing")
    total = lambda rs: money(sum((r["amount"] for r in rs), D0))  # noqa: E731
    net = money(total(operating) + total(investing) + total(financing))
    return {
        "start_date": start,
        "end_date": end,
        "opening_cash": opening,
        "operating": operating,
        "investing": investing,
        "financing": financing,
        "net_operating": total(operating),
        "net_investing": total(investing),
        "net_financing": total(financing),
        "net_change": net,
        "closing_cash": closing,
        "difference": money(opening + net - closing),
        "accounts": [{"id": a.pk, "name": a.name, "amount": account_balance(a, end)} for a in accounts],
    }


# ---------------------------------------------------------------------------
# Trial Balance
# ---------------------------------------------------------------------------
def trial_balance(start: dt.date | None, end: dt.date) -> dict:
    """Closing balance of every account on ``end``, grouped, in Debit / Credit columns.

    Balance-sheet accounts show their closing balance. Income and expense accounts
    show the movement from ``start`` (all time when empty); profit made before
    ``start`` shows as "Profit & Loss A/c" in Capital. Closing stock is left out,
    as in Tally/Busy: opening stock, purchases and sales stand for it.
    """
    from apps.cashbank.models import MoneyAccount
    from apps.cashbank.services import balance as account_balance
    from apps.journal.models import GROUP_NATURE, AccountGroup, LedgerAccount, Nature
    from apps.journal.services import ledger_balances
    from apps.parties.models import Party, PartyType

    def row(name, signed, **extra):
        """``signed`` is debit positive."""
        signed = money(signed)
        return {"name": name, "debit": signed if signed > 0 else money(D0), "credit": -signed if signed < 0 else money(D0), **extra}

    groups: dict[str, dict] = {}

    def add(key, label, r):
        if r["debit"] or r["credit"]:
            groups.setdefault(key, {"key": key, "label": label, "rows": []})["rows"].append(r)

    labels = dict(AccountGroup.choices)
    ledgers = ledger_balances(end)
    by_ledger_group = defaultdict(list)
    for lg in LedgerAccount.objects.order_by("name"):
        if GROUP_NATURE[lg.group] in (Nature.ASSET, Nature.LIABILITY):
            by_ledger_group[lg.group].append(row(lg.name, ledgers.get(lg.pk, D0), id=lg.pk, link="ledger"))

    pl = profit_and_loss(start, end)

    # Capital & liabilities
    for r in by_ledger_group[AccountGroup.CAPITAL]:
        add("CAPITAL", labels["CAPITAL"], r)
    add("CAPITAL", labels["CAPITAL"], row("Difference in opening balances", -(opening_difference() + opening_stock_value(end))))
    if start:
        earlier = profit_and_loss(None, start - dt.timedelta(days=1))["net_profit"]
        add("CAPITAL", labels["CAPITAL"], row("Profit & Loss A/c (before this period)", -earlier, link="profit-loss"))
    for key in ("RESERVES", "LOANS", "CURRENT_LIABILITIES"):
        for r in by_ledger_group[key]:
            add(key, labels[key], r)

    owed = party_owed(end)
    parties = Party.objects.filter(pk__in=owed).order_by("name").only("pk", "name", "type")
    for p in parties.filter(type=PartyType.VENDOR):
        add("SUNDRY_CREDITORS", "Sundry Creditors", row(p.name, -owed[p.pk], id=p.pk, link="vendor"))
    vat = _vat_balance(end)
    add("DUTIES_TAXES", labels["DUTIES_TAXES"], row("VAT", -vat))
    for r in by_ledger_group[AccountGroup.DUTIES_TAXES]:
        add("DUTIES_TAXES", labels["DUTIES_TAXES"], r)

    # Assets
    for key in ("FIXED_ASSETS", "INVESTMENTS", "CURRENT_ASSETS"):
        for r in by_ledger_group[key]:
            add(key, labels[key], r)
    for p in parties.filter(type=PartyType.CUSTOMER):
        add("SUNDRY_DEBTORS", "Sundry Debtors", row(p.name, owed[p.pk], id=p.pk, link="customer"))
    for a in MoneyAccount.objects.order_by("name"):
        add("CASH_BANK", "Cash & Bank", row(a.name, account_balance(a, end), id=a.pk, link="cash"))
    for r in by_ledger_group[AccountGroup.LOANS_ADVANCES]:
        add("LOANS_ADVANCES", labels["LOANS_ADVANCES"], r)

    # Trading and profit & loss accounts for the period
    add("STOCK", "Stock-in-hand", row("Opening stock", pl["opening_stock"] + pl["opening_stock_added"], link="stock"))
    add("SALES", "Sales Accounts", row("Sales (excluding VAT)", -pl["sales"], link="sales"))
    add("PURCHASES", "Purchase Accounts", row("Purchases (excluding VAT)", pl["purchases"], link="purchases"))
    for key, rows, sign in [
        ("DIRECT_INCOME", pl["direct_income"], -1),
        ("DIRECT_EXPENSES", pl["direct_expenses"], 1),
        ("INDIRECT_INCOME", pl["indirect_income"], -1),
    ]:
        for r in rows:
            add(key, labels[key], row(r["name"], sign * r["amount"], id=r["id"], link="ledger"))
    for r in pl["expense_categories"]:
        add("INDIRECT_EXPENSES", labels["INDIRECT_EXPENSES"], row(r["name"], r["amount"], id=r["id"], link="expense-category"))
    for r in pl["indirect_expenses"]:
        add("INDIRECT_EXPENSES", labels["INDIRECT_EXPENSES"], row(r["name"], r["amount"], id=r["id"], link="ledger"))

    out = []
    for g in groups.values():
        net = money(sum((r["debit"] - r["credit"] for r in g["rows"]), D0))
        out.append(g | {"debit": max(net, money(D0)), "credit": max(-net, money(D0)), "count": len(g["rows"])})
    total_debit = money(sum((g["debit"] for g in out), D0))
    total_credit = money(sum((g["credit"] for g in out), D0))
    return {
        "start_date": start,
        "end_date": end,
        "groups": out,
        "total_debit": total_debit,
        "total_credit": total_credit,
        "difference": money(total_debit - total_credit),
    }
