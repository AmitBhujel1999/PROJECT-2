import datetime as dt
from decimal import Decimal as D

import pytest

from apps.cashbank.models import AccountKind, MoneyAccount
from apps.expenses.services import ensure_default_categories
from apps.users.models import Role

from .conftest import client_for, line, make_user


@pytest.fixture
def bank(db):
    return MoneyAccount.objects.create(name="Nabil Bank", kind=AccountKind.BANK, account_number="0123", opening_balance=D("50000"))


@pytest.fixture
def cash(db):
    return MoneyAccount.objects.create(name="Cash in Hand", kind=AccountKind.CASH, opening_balance=D("1000"))


def book(api, account, **params):
    r = api.get(f"/api/cash-bank/accounts/{account.pk}/book/", params)
    assert r.status_code == 200, r.content
    return r.json()["data"]


def test_account_crud_and_unique_name(api):
    r = api.post("/api/cash-bank/accounts/", {"name": "eSewa", "kind": "WALLET", "account_number": "9800000000"}, format="json")
    assert r.status_code == 201, r.content
    data = r.json()["data"]
    assert data["kind_display"] == "Digital wallet" and data["balance"] == "0.00"
    assert api.post("/api/cash-bank/accounts/", {"name": "esewa", "kind": "WALLET"}, format="json").status_code == 400
    r = api.patch(f"/api/cash-bank/accounts/{data['id']}/", {"opening_balance": "2500"}, format="json")
    assert r.json()["data"]["balance"] == "2500.00"
    assert api.delete(f"/api/cash-bank/accounts/{data['id']}/").status_code == 200


def test_receipt_payment_expense_and_contra_in_book(api, bank, cash, customer, vendor):
    receipt = {"party": customer.pk, "date": "2026-09-02", "amount": "12000", "account": bank.pk, "reference_number": "TRF-1"}
    r = api.post("/api/customer-receipts/", receipt, format="json")
    assert r.status_code == 201, r.content
    assert r.json()["data"]["account_name"] == "Nabil Bank"
    assert r.json()["data"]["payment_method"] == "BANK"

    payment = {"party": vendor.pk, "date": "2026-09-05", "amount": "20000", "account": bank.pk}
    assert api.post("/api/vendor-payments/", payment, format="json").status_code == 201

    rent = ensure_default_categories()["Rent"]
    expense = {"date": "2026-09-12", "category": rent.pk, "description": "Office rent", "amount": "15000", "account": bank.pk}
    r = api.post("/api/expenses/", expense, format="json")
    assert r.status_code == 201 and r.json()["data"]["account_name"] == "Nabil Bank"

    contra = {"date": "2026-09-09", "from_account": cash.pk, "to_account": bank.pk, "amount": "500", "notes": "Cash deposit"}
    r = api.post("/api/cash-bank/contras/", contra, format="json")
    assert r.status_code == 201, r.content
    contra_id = r.json()["data"]["id"]

    data = book(api, bank)
    assert data["opening_balance"] == "50000.00"
    assert [(e["voucher_type"], e["money_in"], e["money_out"], e["balance"]) for e in data["entries"]] == [
        ("Receipt", "12000.00", "0.00", "62000.00"),
        ("Payment", "0.00", "20000.00", "42000.00"),
        ("Contra", "500.00", "0.00", "42500.00"),
        ("Expense", "0.00", "15000.00", "27500.00"),
    ]
    assert data["entries"][0]["particulars"] == "ABC Traders"
    assert data["entries"][2]["particulars"] == "Transfer from Cash in Hand"
    assert data["closing_balance"] == "27500.00"
    assert book(api, cash)["closing_balance"] == "500.00"

    # A period starts from the balance carried forward.
    period = book(api, bank, start_date="2026-09-06", end_date="2026-09-30")
    assert period["opening_balance"] == "42000.00" and len(period["entries"]) == 2

    # Cancelling removes the entry from the book.
    assert api.post(f"/api/cash-bank/contras/{contra_id}/cancel/", {"reason": "wrong"}, format="json").status_code == 200
    assert book(api, cash)["closing_balance"] == "1000.00"
    accounts = {a["name"]: a["balance"] for a in api.get("/api/cash-bank/accounts/").json()["data"]["results"]}
    assert accounts == {"Cash in Hand": "1000.00", "Nabil Bank": "27000.00"}


def test_contra_rules(api, bank, cash):
    same = {"date": "2026-09-09", "from_account": bank.pk, "to_account": bank.pk, "amount": "10"}
    assert api.post("/api/cash-bank/contras/", same, format="json").status_code == 400
    cash.is_active = False
    cash.save()
    body = {"date": "2026-09-09", "from_account": cash.pk, "to_account": bank.pk, "amount": "10"}
    assert api.post("/api/cash-bank/contras/", body, format="json").json()["error"]["code"] == "ACCOUNT_INACTIVE"


def test_used_account_cannot_be_deleted(api, bank, customer):
    api.post("/api/customer-receipts/", {"party": customer.pk, "date": "2026-09-02", "amount": "10", "account": bank.pk}, format="json")
    r = api.delete(f"/api/cash-bank/accounts/{bank.pk}/")
    assert r.status_code == 409 and r.json()["error"]["code"] == "ACCOUNT_IN_USE"


def test_old_callers_without_account_use_default_accounts(api, customer):
    r = api.post("/api/customer-receipts/", {"party": customer.pk, "date": "2026-09-02", "amount": "10", "payment_method": "CASH"}, format="json")
    assert r.json()["data"]["account_name"] == "Cash in Hand"
    r = api.post("/api/customer-receipts/", {"party": customer.pk, "date": "2026-09-02", "amount": "10", "payment_method": "CHEQUE"}, format="json")
    assert r.json()["data"]["account_name"] == "Bank" and r.json()["data"]["payment_method"] == "CHEQUE"


def test_sale_paid_into_chosen_account(api, bank, customer, product_factory):
    p = product_factory(price=D("1000"), tax=D("0"))
    body = {"party": customer.pk, "date": "2026-09-27", "items": [line(p, 2)], "payment": {"pay_in_full": True, "account": bank.pk}}
    assert api.post("/api/sales/", body, format="json").status_code == 201
    entries = book(api, bank)["entries"]
    assert [(e["voucher_type"], e["money_in"]) for e in entries] == [("Receipt", "2000.00")]


def test_day_book(api, bank, customer, vendor, product_factory):
    p = product_factory(price=D("1000"), tax=D("0"))
    api.post("/api/sales/", {"party": customer.pk, "date": "2026-09-27", "items": [line(p, 1)]}, format="json")
    api.post("/api/customer-receipts/", {"party": customer.pk, "date": "2026-09-27", "amount": "400", "account": bank.pk}, format="json")
    api.post("/api/customer-receipts/", {"party": customer.pk, "date": "2026-09-26", "amount": "1", "account": bank.pk}, format="json")
    data = api.get("/api/reports/day-book/", {"start_date": "2026-09-27"}).json()["data"]
    assert [(e["voucher_type"], e["amount"], e["account_name"]) for e in data["entries"]] == [
        ("Sales", "1000.00", ""),
        ("Receipt", "400.00", "Nabil Bank"),
    ]
    assert {t["voucher_type"]: t["amount"] for t in data["totals"]} == {"Sales": "1000.00", "Receipt": "400.00"}
    csv = api.get("/api/reports/day-book/", {"start_date": "2026-09-27", "export": "csv"})
    assert csv.status_code == 200 and b"Nabil Bank" in csv.content
    pdf = api.get(f"/api/cash-bank/accounts/{bank.pk}/book/", {"export": "pdf"})
    assert pdf.content.startswith(b"%PDF")


@pytest.mark.parametrize(
    "role, method, url, expected",
    [
        (Role.STAFF, "get", "/api/cash-bank/accounts/", 200),
        (Role.STAFF, "get", "/api/cash-bank/contras/", 403),
        (Role.STAFF, "get", "/api/reports/day-book/", 403),
        (Role.ACCOUNTANT, "post", "/api/cash-bank/accounts/", 403),
        (Role.ACCOUNTANT, "get", "/api/cash-bank/contras/", 200),
        (Role.MANAGER, "post", "/api/cash-bank/accounts/", 400),
    ],
)
def test_permissions(role, method, url, expected, db):
    c = client_for(make_user(role, f"cb_{role.lower()}"))
    assert getattr(c, method)(url, {}, format="json").status_code == expected


def test_staff_sees_accounts_without_balances(bank, db):
    c = client_for(make_user(Role.STAFF, "cb_staff2"))
    assert c.get("/api/cash-bank/accounts/").json()["data"]["results"][0]["balance"] is None
    assert c.get(f"/api/cash-bank/accounts/{bank.pk}/book/").status_code == 403


def test_default_cash_account_is_created_for_first_expense(api):
    rent = ensure_default_categories()["Rent"]
    body = {"date": dt.date(2026, 9, 1).isoformat(), "category": rent.pk, "description": "Tea", "amount": "100"}
    assert api.post("/api/expenses/", body, format="json").json()["data"]["account_name"] == "Cash in Hand"
