from decimal import Decimal as D

import pytest

from apps.cashbank.models import AccountKind, MoneyAccount
from apps.expenses.services import ensure_default_categories
from apps.journal.models import LedgerAccount

from .conftest import line


@pytest.fixture
def books(api, customer, vendor, product_factory):
    """A small set of books touching every kind of entry."""
    bank = MoneyAccount.objects.create(name="Nabil Bank", kind=AccountKind.BANK, opening_balance=D("100000"))
    p = product_factory(stock=D("10"), cost=D("100"), price=D("200"), tax=D("13"))
    # Purchase 10 @ 130 + 13% VAT (unpaid).
    r = api.post("/api/purchases/", {"party": vendor.pk, "date": "2026-09-02", "items": [line(p, 10, 130)]}, format="json")
    assert r.status_code == 201, r.content
    # Sale 5 @ 200 + 13% VAT, paid in full into the bank.
    sale = {"party": customer.pk, "date": "2026-09-05", "items": [line(p, 5)], "payment": {"pay_in_full": True, "account": bank.pk}}
    assert api.post("/api/sales/", sale, format="json").status_code == 201
    # A sale that is later cancelled must not count anywhere.
    cancelled = api.post("/api/sales/", {"party": customer.pk, "date": "2026-09-06", "items": [line(p, 2)]}, format="json").json()["data"]
    assert api.post(f"/api/sales/{cancelled['id']}/cancel/", {"reason": "test"}, format="json").status_code == 200
    # Pay the vendor 500, pay rent 1,000 (no VAT).
    assert api.post("/api/vendor-payments/", {"party": vendor.pk, "date": "2026-09-07", "amount": "500", "account": bank.pk}, format="json").status_code == 201
    rent = ensure_default_categories()["Rent"]
    exp = {"date": "2026-09-08", "category": rent.pk, "description": "Rent", "amount": "1000", "account": bank.pk}
    assert api.post("/api/expenses/", exp, format="json").status_code == 201
    # Journals: furniture brought in by the owner; a bank loan received into the bank.
    furniture = LedgerAccount.objects.create(name="Furniture T", group="FIXED_ASSETS", opening_side="DR")
    capital = LedgerAccount.objects.create(name="Capital T", group="CAPITAL", opening_side="CR")
    loan = LedgerAccount.objects.create(name="Loan T", group="LOANS", opening_side="CR")
    for lines in (
        [{"account": f"ledger:{furniture.pk}", "debit": "5000"}, {"account": f"ledger:{capital.pk}", "credit": "5000"}],
        [{"account": f"cash:{bank.pk}", "debit": "20000"}, {"account": f"ledger:{loan.pk}", "credit": "20000"}],
    ):
        assert api.post("/api/journal/vouchers/", {"date": "2026-09-10", "lines": lines}, format="json").status_code == 201
    return {"bank": bank, "product": p}


def test_profit_and_loss_uses_weighted_average_cost(api, books):
    d = api.get("/api/reports/profit-loss/", {"end_date": "2026-09-30"}).json()["data"]
    # Average cost = (10 x 100 + 10 x 130) / 20 = 115; 15 left = 1,725; 5 sold cost 575.
    assert d["sales"] == "1000.00"
    assert d["purchases"] == "1300.00"
    assert d["opening_stock_added"] == "1000.00"
    assert d["closing_stock"] == "1725.00"
    assert d["cost_of_goods_sold"] == "575.00"
    assert d["gross_profit"] == "425.00"
    assert [(c["name"], c["amount"]) for c in d["expense_categories"]] == [("Rent", "1000.00")]
    assert d["net_profit"] == "-575.00"

    # A period starting after the opening: opening stock is the stock value on the day before.
    period = api.get("/api/reports/profit-loss/", {"start_date": "2026-09-03", "end_date": "2026-09-30"}).json()["data"]
    assert period["opening_stock"] == "2300.00" and period["purchases"] == "0.00" and period["sales"] == "1000.00"


def test_balance_sheet_balances(api, books):
    d = api.get("/api/reports/balance-sheet/", {"as_of": "2026-09-30"}).json()["data"]
    assert d["difference"] == "0.00", d
    assert d["total_assets"] == d["total_liabilities"]
    rows = {r["name"]: r["amount"] for g in d["liabilities"] + d["assets"] for r in g["rows"]}
    # Bank: 100,000 + 1,130 sale - 500 - 1,000 + 20,000 loan.
    assert rows["Nabil Bank"] == "119630.00"
    assert rows["Closing stock"] == "1725.00"
    assert rows["Sundry Creditors"] == "969.00"  # 1,469 bill - 500 paid
    assert rows["VAT receivable"] == "39.00"  # 130 collected on the sale - 169 paid on the purchase
    assert rows["Furniture T"] == "5000.00" and rows["Capital T"] == "5000.00" and rows["Loan T"] == "20000.00"
    assert rows["Profit & Loss A/c"] == "-575.00"
    # Openings: bank 100,000 + opening stock 1,000.
    assert rows["Difference in opening balances"] == "101000.00"
    assert "Sundry Debtors" not in rows


def test_cash_flow_reconciles_with_cash_books(api, books):
    d = api.get("/api/reports/cash-flow/", {"start_date": "2026-09-01", "end_date": "2026-09-30"}).json()["data"]
    assert d["opening_cash"] == "100000.00"
    assert {r["name"]: r["amount"] for r in d["operating"]} == {
        "Received from customers": "1130.00", "Paid to suppliers": "-500.00", "Expenses paid": "-1000.00",
    }
    assert d["financing"] == [{"name": "Loan T", "amount": "20000.00"}]
    assert d["closing_cash"] == "119630.00" and d["difference"] == "0.00"
    assert api.get("/api/reports/cash-flow/", {"end_date": "2026-09-30"}).status_code == 400


@pytest.mark.parametrize("url", ["/api/reports/profit-loss/", "/api/reports/balance-sheet/", "/api/reports/cash-flow/"])
def test_exports(api, books, url):
    params = {"start_date": "2026-09-01", "end_date": "2026-09-30", "as_of": "2026-09-30"}
    csv = api.get(url, {**params, "export": "csv"})
    assert csv.status_code == 200 and b"Particulars" in csv.content
    assert api.get(url, {**params, "export": "pdf"}).content.startswith(b"%PDF")


def test_staff_cannot_see_statements(db):
    from apps.users.models import Role

    from .conftest import client_for, make_user

    c = client_for(make_user(Role.STAFF, "st_staff"))
    assert c.get("/api/reports/balance-sheet/").status_code == 403
