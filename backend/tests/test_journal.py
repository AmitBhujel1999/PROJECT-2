from decimal import Decimal as D

import pytest

from apps.cashbank.models import AccountKind, MoneyAccount
from apps.expenses.services import ensure_default_categories
from apps.journal.models import LedgerAccount
from apps.users.models import Role

from .conftest import client_for, line, make_user


@pytest.fixture
def capital(db):
    return LedgerAccount.objects.create(name="Owner Capital X", group="CAPITAL", opening_side="CR")


@pytest.fixture
def furniture(db):
    return LedgerAccount.objects.create(name="Furniture X", group="FIXED_ASSETS", opening_side="DR", opening_balance=D("1000"))


@pytest.fixture
def bank(db):
    return MoneyAccount.objects.create(name="Nabil Bank", kind=AccountKind.BANK, opening_balance=D("50000"))


def jv(api, lines, date="2026-09-10", narration="test"):
    return api.post("/api/journal/vouchers/", {"date": date, "narration": narration, "lines": lines}, format="json")


def L(account, debit="0", credit="0", narration=""):  # noqa: N802
    return {"account": account, "debit": debit, "credit": credit, "narration": narration}


def test_ledger_crud_defaults_side_and_unique_name(api):
    r = api.post("/api/journal/ledgers/", {"name": "Vehicle", "group": "FIXED_ASSETS", "opening_balance": "500"}, format="json")
    assert r.status_code == 201, r.content
    d = r.json()["data"]
    assert d["opening_side"] == "DR" and d["balance"] == {"amount": "500.00", "side": "Dr"} and d["nature"] == "ASSET"
    r = api.post("/api/journal/ledgers/", {"name": "Partner Loan", "group": "LOANS"}, format="json")
    assert r.json()["data"]["opening_side"] == "CR"
    assert api.post("/api/journal/ledgers/", {"name": "vehicle", "group": "FIXED_ASSETS"}, format="json").status_code == 400
    groups = api.get("/api/journal/ledgers/groups/").json()["data"]
    assert {"value": "CAPITAL", "label": "Capital Account", "nature": "LIABILITY"} in groups


def test_voucher_must_balance_and_have_one_side_per_line(api, capital, furniture):
    r = jv(api, [L(f"ledger:{furniture.pk}", debit="100"), L(f"ledger:{capital.pk}", credit="90")])
    assert r.status_code == 400 and r.json()["error"]["code"] == "NOT_BALANCED"
    r = jv(api, [L(f"ledger:{furniture.pk}", debit="100", credit="100"), L(f"ledger:{capital.pk}", credit="0")])
    assert r.json()["error"]["code"] == "INVALID_LINE"
    assert jv(api, [L(f"ledger:{furniture.pk}", debit="100")]).json()["error"]["code"] == "TOO_FEW_LINES"
    assert jv(api, [L("ledger:999999", debit="1"), L(f"ledger:{capital.pk}", credit="1")]).json()["error"]["code"] == "ACCOUNT_NOT_FOUND"
    assert jv(api, [L("bogus", debit="1"), L(f"ledger:{capital.pk}", credit="1")]).json()["error"]["code"] == "ACCOUNT_REQUIRED"


def test_ledger_book_and_cancel(api, capital, furniture):
    r = jv(api, [L(f"ledger:{furniture.pk}", debit="25000"), L(f"ledger:{capital.pk}", credit="25000")], narration="Owner brought furniture")
    assert r.status_code == 201, r.content
    v = r.json()["data"]
    assert v["voucher_number"].startswith("JV-") and v["total_amount"] == "25000.00"
    assert [x["account"]["name"] for x in v["lines"]] == ["Furniture X", "Owner Capital X"]

    book = api.get(f"/api/journal/ledgers/{furniture.pk}/book/").json()["data"]
    assert book["opening"] == {"amount": "1000.00", "side": "Dr"}
    assert book["closing"] == {"amount": "26000.00", "side": "Dr"}
    assert book["entries"][0]["narration"] == "Owner brought furniture"
    cap = api.get(f"/api/journal/ledgers/{capital.pk}/").json()["data"]
    assert cap["balance"] == {"amount": "25000.00", "side": "Cr"}

    listing = api.get("/api/journal/vouchers/").json()["data"]["results"][0]
    assert listing["accounts"] == {"debit": ["Furniture X"], "credit": ["Owner Capital X"]}
    assert api.get(f"/api/journal/vouchers/{v['id']}/pdf/").content.startswith(b"%PDF")
    assert api.get(f"/api/journal/ledgers/{furniture.pk}/book/", {"export": "csv"}).status_code == 200

    assert api.post(f"/api/journal/vouchers/{v['id']}/cancel/", {"reason": "wrong"}, format="json").status_code == 200
    assert api.get(f"/api/journal/ledgers/{capital.pk}/").json()["data"]["balance"] == {"amount": "0.00", "side": "Dr"}
    assert api.delete(f"/api/journal/ledgers/{capital.pk}/").status_code == 409


def test_cash_bank_lines_show_in_book_and_day_book(api, capital, bank):
    r = jv(api, [L(f"cash:{bank.pk}", debit="10000"), L(f"ledger:{capital.pk}", credit="10000", narration="Capital introduced")],
           date="2026-09-27")
    assert r.status_code == 201
    book = api.get(f"/api/cash-bank/accounts/{bank.pk}/book/").json()["data"]
    assert [(e["voucher_type"], e["money_in"]) for e in book["entries"]] == [("Journal", "10000.00")]
    assert book["closing_balance"] == "60000.00"
    accounts = {a["name"]: a["balance"] for a in api.get("/api/cash-bank/accounts/").json()["data"]["results"]}
    assert accounts["Nabil Bank"] == "60000.00"
    day = api.get("/api/reports/day-book/", {"start_date": "2026-09-27"}).json()["data"]
    assert [(e["voucher_type"], e["amount"], e["link"]) for e in day["entries"]] == [("Journal", "10000.00", "journal")]


def test_party_lines_change_balance_ledger_and_aging(api, customer, vendor, capital, product_factory):
    p = product_factory(price=D("1000"), tax=D("0"))
    api.post("/api/sales/", {"party": customer.pk, "date": "2026-09-01", "items": [line(p, 2)]}, format="json")
    # Discount allowed: credit the customer 300 (they owe less), debit an expense category.
    discount = ensure_default_categories()["Miscellaneous"]
    r = jv(api, [L(f"expense:{discount.pk}", debit="300"), L(f"party:{customer.pk}", credit="300")], date="2026-09-05")
    assert r.status_code == 201, r.content
    # Vendor: we owe them 500 more (credit vendor, debit capital just for the test).
    assert jv(api, [L(f"ledger:{capital.pk}", debit="500"), L(f"party:{vendor.pk}", credit="500")], date="2026-09-05").status_code == 201

    ledger = api.get(f"/api/customers/{customer.pk}/ledger/").json()["data"]
    rows = ledger["results"]
    assert [(e["transaction_type"], e["debit"], e["credit"]) for e in rows][-1] == ("Journal", "0.00", "300.00")
    assert ledger["closing_balance"] == "1700.00"

    summary = api.get(f"/api/customers/{customer.pk}/summary/").json()["data"]
    assert summary["journal"] == "-300.00" and summary["net_balance"] == "1700.00"
    aging = api.get("/api/reports/receivables-aging/", {"as_of": "2026-09-30"}).json()["data"]
    row = next(r for r in aging["results"] if r["party_id"] == customer.pk)
    assert row["journal"] == "-300.00" and row["net_balance"] == "1700.00"
    vendor_summary = api.get(f"/api/vendors/{vendor.pk}/summary/").json()["data"]
    assert vendor_summary["journal"] == "500.00" and vendor_summary["net_balance"] == "500.00"

    report = api.get("/api/reports/expenses/", {"start_date": "2026-09-01", "end_date": "2026-09-30"}).json()["data"]
    s = report["summary"]
    assert s["journal_amount"] == "300.00"
    assert {c["category_name"]: c["total_with_journal"] for c in s["by_category"]}[discount.name] == "300.00"


def test_account_search(api, capital, bank, customer):
    keys = {o["key"] for o in api.get("/api/journal/vouchers/accounts/").json()["data"]}
    assert {f"ledger:{capital.pk}", f"cash:{bank.pk}", f"party:{customer.pk}"} <= keys
    found = api.get("/api/journal/vouchers/accounts/", {"search": "nabil"}).json()["data"]
    assert [o["name"] for o in found] == ["Nabil Bank"]


@pytest.mark.parametrize(
    "role, method, url, expected",
    [
        (Role.STAFF, "get", "/api/journal/vouchers/", 403),
        (Role.STAFF, "get", "/api/journal/ledgers/", 403),
        (Role.ACCOUNTANT, "get", "/api/journal/vouchers/", 200),
        (Role.ACCOUNTANT, "post", "/api/journal/ledgers/", 403),
        (Role.MANAGER, "post", "/api/journal/ledgers/", 400),
    ],
)
def test_permissions(role, method, url, expected, db):
    c = client_for(make_user(role, f"jv_{role.lower()}"))
    assert getattr(c, method)(url, {}, format="json").status_code == expected
