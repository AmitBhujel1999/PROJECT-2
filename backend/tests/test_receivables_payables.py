"""Receipts, vendor payments, allocation, ledgers, statements and aging."""

import datetime as dt
from decimal import Decimal as D

import pytest
from django.utils import timezone

from apps.receivables.services import (
    calculate_customer_aging,
    calculate_customer_ledger,
    create_customer_receipt,
    customer_aging_detail,
    customer_balance,
)
from apps.payables.services import calculate_vendor_aging, calculate_vendor_ledger, create_vendor_payment
from apps.sales.models import Sale

from .conftest import item

pytestmark = pytest.mark.django_db


@pytest.fixture
def zero_tax_product(product_factory):
    # Tax 0% and price 1 so invoice totals equal quantities (easy arithmetic).
    return product_factory("Unit Item", stock=1_000_000, price=1, cost=1, tax=0, opening_date=dt.date(2024, 1, 1))


def invoice(make_sale, customer, product, amount, date, due=None):
    sale = make_sale(customer, [item(product, amount)], date=date)
    if due:
        Sale.objects.filter(pk=sale.pk).update(due_date=due)
        sale.refresh_from_db()
    return sale


# ---------------------------------------------------------------------------
# Allocation
# ---------------------------------------------------------------------------
def test_spec_allocation_example(api, customer, zero_tax_product, make_sale):
    inv1 = invoice(make_sale, customer, zero_tax_product, 30000, dt.date(2026, 8, 1))
    inv8 = invoice(make_sale, customer, zero_tax_product, 20000, dt.date(2026, 8, 5))
    inv15 = invoice(make_sale, customer, zero_tax_product, 15000, dt.date(2026, 8, 10))
    body = {
        "party": customer.pk, "date": "2026-09-01", "amount": "40000", "payment_method": "BANK",
        "allocations": [{"document": inv1.pk, "amount": "30000"}, {"document": inv8.pk, "amount": "10000"}],
    }
    r = api.post("/api/customer-receipts/", body, format="json")
    assert r.status_code == 201, r.json()
    d = r.json()["data"]
    assert d["receipt_number"] == "RCPT-000001"
    assert d["unallocated_amount"] == "0.00"
    for s in (inv1, inv8, inv15):
        s.refresh_from_db()
    assert (inv1.payment_status, inv1.balance_due) == ("PAID", D("0.00"))
    assert (inv8.payment_status, inv8.balance_due) == ("PARTIAL", D("10000.00"))
    assert (inv15.payment_status, inv15.balance_due) == ("UNPAID", D("15000.00"))
    assert customer_balance(customer.pk, dt.date(2026, 9, 30)) == D("25000.00")


def test_over_allocation_rejected(api, customer, zero_tax_product, make_sale):
    inv = invoice(make_sale, customer, zero_tax_product, 1000, dt.date(2026, 8, 1))
    body = {"party": customer.pk, "date": "2026-09-01", "amount": "500", "payment_method": "CASH",
            "allocations": [{"document": inv.pk, "amount": "600"}]}
    r = api.post("/api/customer-receipts/", body, format="json")
    assert r.status_code == 400 and r.json()["error"]["code"] == "INVALID_ALLOCATION"
    body = {**body, "amount": "2000", "allocations": [{"document": inv.pk, "amount": "1500"}]}
    r = api.post("/api/customer-receipts/", body, format="json")
    assert r.status_code == 400 and "outstanding balance" in r.json()["error"]["message"]
    from apps.receivables.models import CustomerReceipt

    assert CustomerReceipt.objects.count() == 0  # rolled back


def test_cannot_allocate_to_other_customers_invoice(api, customer, customer2, zero_tax_product, make_sale):
    inv = invoice(make_sale, customer2, zero_tax_product, 100, dt.date(2026, 8, 1))
    r = api.post("/api/customer-receipts/", {"party": customer.pk, "date": "2026-09-01", "amount": "100",
                                             "allocations": [{"document": inv.pk, "amount": "100"}]}, format="json")
    assert r.json()["error"]["code"] == "INVALID_ALLOCATION"


def test_unallocated_receipt_is_advance_and_can_be_allocated_later(api, customer, zero_tax_product, make_sale):
    r = api.post("/api/customer-receipts/", {"party": customer.pk, "date": "2026-09-01", "amount": "5000"}, format="json")
    rid = r.json()["data"]["id"]
    assert r.json()["data"]["unallocated_amount"] == "5000.00"
    aging = calculate_customer_aging(dt.date(2026, 9, 27))
    assert aging["totals"]["total"] == D("0.00")
    assert aging["totals"]["unallocated"] == D("5000.00")
    assert aging["totals"]["net_balance"] == D("-5000.00")

    inv = invoice(make_sale, customer, zero_tax_product, 3000, dt.date(2026, 9, 10))
    r = api.post(f"/api/customer-receipts/{rid}/allocate/", {"allocations": [{"document": inv.pk, "amount": "3000"}]}, format="json")
    assert r.status_code == 200
    assert r.json()["data"]["unallocated_amount"] == "2000.00"
    inv.refresh_from_db()
    assert inv.payment_status == "PAID"
    # The allocation is effective from the later of receipt and invoice date
    assert r.json()["data"]["allocations"][0]["date"] == "2026-09-10"


def test_auto_allocate_oldest_due_first(api, customer, zero_tax_product, make_sale):
    old = invoice(make_sale, customer, zero_tax_product, 100, dt.date(2026, 7, 1))
    new = invoice(make_sale, customer, zero_tax_product, 100, dt.date(2026, 8, 1))
    r = api.post("/api/customer-receipts/", {"party": customer.pk, "date": "2026-09-01", "amount": "150", "auto_allocate": True}, format="json")
    old.refresh_from_db()
    new.refresh_from_db()
    assert (old.payment_status, new.payment_status) == ("PAID", "PARTIAL")
    assert new.balance_due == D("50.00")
    assert r.json()["data"]["unallocated_amount"] == "0.00"


def test_unallocate_and_cancel_receipt(api, customer, zero_tax_product, make_sale):
    inv = invoice(make_sale, customer, zero_tax_product, 100, dt.date(2026, 8, 1))
    r = api.post("/api/customer-receipts/", {"party": customer.pk, "date": "2026-09-01", "amount": "100",
                                             "allocations": [{"document": inv.pk, "amount": "100"}]}, format="json")
    rid, alloc_id = r.json()["data"]["id"], r.json()["data"]["allocations"][0]["id"]
    r = api.post(f"/api/customer-receipts/{rid}/unallocate/", {"allocation": alloc_id}, format="json")
    assert r.json()["data"]["unallocated_amount"] == "100.00"
    inv.refresh_from_db()
    assert inv.payment_status == "UNPAID"
    api.post(f"/api/customer-receipts/{rid}/allocate/", {"allocations": [{"document": inv.pk, "amount": "100"}]}, format="json")
    r = api.post(f"/api/customer-receipts/{rid}/cancel/", {"reason": "Cheque bounced"}, format="json")
    assert r.json()["data"]["status"] == "CANCELLED"
    inv.refresh_from_db()
    assert (inv.payment_status, inv.balance_due) == ("UNPAID", D("100.00"))
    # Allocations are voided, never deleted
    assert len(r.json()["data"]["allocations"]) == 2
    assert all(not a["is_active"] for a in r.json()["data"]["allocations"])


def test_partial_then_full_payment(accountant, customer, zero_tax_product, make_sale):
    inv = invoice(make_sale, customer, zero_tax_product, 1000, dt.date(2026, 8, 1))
    create_customer_receipt(customer_id=customer.pk, date=dt.date(2026, 8, 10), amount=D("400"), payment_method="CASH",
                            allocations=[{"document": inv.pk, "amount": D("400")}], user=accountant)
    inv.refresh_from_db()
    assert (inv.payment_status, inv.amount_paid) == ("PARTIAL", D("400.00"))
    create_customer_receipt(customer_id=customer.pk, date=dt.date(2026, 8, 20), amount=D("600"), payment_method="BANK",
                            allocations=[{"document": inv.pk, "amount": D("600")}], user=accountant)
    inv.refresh_from_db()
    assert (inv.payment_status, inv.balance_due) == ("PAID", D("0.00"))


# ---------------------------------------------------------------------------
# Vendor payments
# ---------------------------------------------------------------------------
def test_vendor_payment_partial_full_and_advance(api, vendor, product_factory, make_purchase):
    p = product_factory(tax=0)
    b1 = make_purchase(vendor, [item(p, 10, 100)], date=dt.date(2026, 8, 1))
    b2 = make_purchase(vendor, [item(p, 5, 100)], date=dt.date(2026, 8, 15))
    r = api.post("/api/vendor-payments/", {"party": vendor.pk, "date": "2026-09-01", "amount": "1200", "payment_method": "CHEQUE",
                                           "reference_number": "CHQ-9", "allocations": [{"document": b1.pk, "amount": "1000"},
                                                                                         {"document": b2.pk, "amount": "200"}]}, format="json")
    assert r.status_code == 201, r.json()
    assert r.json()["data"]["payment_number"] == "PAY-000001"
    b1.refresh_from_db()
    b2.refresh_from_db()
    assert (b1.payment_status, b2.payment_status) == ("PAID", "PARTIAL")
    r = api.post("/api/vendor-payments/", {"party": vendor.pk, "date": "2026-09-05", "amount": "800"}, format="json")
    assert r.json()["data"]["unallocated_amount"] == "800.00"
    aging = calculate_vendor_aging(dt.date(2026, 9, 27))
    assert aging["totals"]["total"] == D("300.00")
    assert aging["totals"]["unallocated"] == D("800.00")
    assert aging["totals"]["net_balance"] == D("-500.00")


# ---------------------------------------------------------------------------
# Ledgers
# ---------------------------------------------------------------------------
def test_customer_ledger_running_balance_spec_example(api, accountant, customer, zero_tax_product, make_sale):
    invoice(make_sale, customer, zero_tax_product, 25000, dt.date(2026, 8, 20))  # before period -> opening 25,000
    invoice(make_sale, customer, zero_tax_product, 50000, dt.date(2026, 9, 1))
    create_customer_receipt(customer_id=customer.pk, date=dt.date(2026, 9, 5), amount=D("20000"), payment_method="CASH", user=accountant)
    invoice(make_sale, customer, zero_tax_product, 25000, dt.date(2026, 9, 12))
    create_customer_receipt(customer_id=customer.pk, date=dt.date(2026, 9, 20), amount=D("10000"), payment_method="BANK", user=accountant)

    data = calculate_customer_ledger(customer.pk, start=dt.date(2026, 9, 1), end=dt.date(2026, 9, 27))
    assert data["opening_balance"] == D("25000.00")
    rows = [(e["reference"], e["transaction_type"], e["debit"], e["credit"], e["balance"]) for e in data["entries"]]
    assert rows == [
        ("INV-000002", "Invoice", D("50000.00"), D("0.00"), D("75000.00")),
        ("RCPT-000001", "Receipt", D("0.00"), D("20000.00"), D("55000.00")),
        ("INV-000003", "Invoice", D("25000.00"), D("0.00"), D("80000.00")),
        ("RCPT-000002", "Receipt", D("0.00"), D("10000.00"), D("70000.00")),
    ]
    assert data["closing_balance"] == D("70000.00")
    # API: statement and paginated ledger
    r = api.get(f"/api/customers/{customer.pk}/statement/?start_date=2026-09-01&end_date=2026-09-27").json()["data"]
    assert r["opening_balance"] == "25000.00" and r["closing_balance"] == "70000.00"
    r = api.get(f"/api/customers/{customer.pk}/ledger/?start_date=2026-09-01&page_size=25").json()["data"]
    assert r["count"] == 4 and r["results"][0]["due_date"] == "2026-10-01"


def test_ledger_shows_cancellation_as_reversal(api, customer, zero_tax_product, make_sale):
    sale = invoice(make_sale, customer, zero_tax_product, 500, dt.date(2026, 9, 1))
    api.post(f"/api/sales/{sale.pk}/cancel/", {"reason": "Error"}, format="json")
    data = calculate_customer_ledger(customer.pk)
    assert [e["transaction_type"] for e in data["entries"]] == ["Invoice", "Invoice Cancellation"]
    assert data["closing_balance"] == D("0.00")
    today = timezone.localdate()
    assert customer_balance(customer.pk, today) == D("0.00")
    # Before the cancellation date the invoice was still owed (history is not rewritten)
    assert customer_balance(customer.pk, dt.date(2026, 9, 1)) == D("500.00")


def test_vendor_ledger_credit_bills_debit_payments(accountant, vendor, product_factory, make_purchase):
    p = product_factory(tax=0)
    make_purchase(vendor, [item(p, 10, 100)], date=dt.date(2026, 9, 1))
    create_vendor_payment(vendor_id=vendor.pk, date=dt.date(2026, 9, 10), amount=D("400"), payment_method="BANK", user=accountant)
    data = calculate_vendor_ledger(vendor.pk)
    e1, e2 = data["entries"]
    assert (e1["credit"], e1["debit"], e1["balance"]) == (D("1000.00"), D("0.00"), D("1000.00"))
    assert (e2["credit"], e2["debit"], e2["balance"]) == (D("0.00"), D("400.00"), D("600.00"))


def test_historical_balance(accountant, customer, zero_tax_product, make_sale):
    invoice(make_sale, customer, zero_tax_product, 1000, dt.date(2026, 7, 1))
    create_customer_receipt(customer_id=customer.pk, date=dt.date(2026, 8, 1), amount=D("300"), payment_method="CASH", user=accountant)
    assert customer_balance(customer.pk, dt.date(2026, 6, 30)) == D("0.00")
    assert customer_balance(customer.pk, dt.date(2026, 7, 15)) == D("1000.00")
    assert customer_balance(customer.pk, dt.date(2026, 8, 1)) == D("700.00")


# ---------------------------------------------------------------------------
# Aging
# ---------------------------------------------------------------------------
AS_OF = dt.date(2026, 9, 27)


@pytest.mark.parametrize(
    "days_overdue,bucket",
    [(-5, "current"), (0, "current"), (1, "days_1_30"), (30, "days_1_30"), (31, "days_31_60"), (60, "days_31_60"),
     (61, "days_61_90"), (90, "days_61_90"), (91, "days_91_120"), (120, "days_91_120"), (121, "days_over_120"), (400, "days_over_120")],
)
def test_aging_buckets(customer, zero_tax_product, make_sale, days_overdue, bucket):
    due = AS_OF - dt.timedelta(days=days_overdue)
    invoice(make_sale, customer, zero_tax_product, 100, min(due, AS_OF) - dt.timedelta(days=1), due=due)
    data = calculate_customer_aging(AS_OF)
    row = data["rows"][0]
    assert row[bucket] == D("100.00")
    assert row["total"] == D("100.00")
    assert sum(row[b["key"]] for b in data["buckets"]) == D("100.00")


def test_partially_paid_invoice_ages_only_outstanding(accountant, customer, zero_tax_product, make_sale):
    inv = invoice(make_sale, customer, zero_tax_product, 1000, dt.date(2026, 7, 1))  # due 2026-07-31 -> 58 days
    create_customer_receipt(customer_id=customer.pk, date=dt.date(2026, 8, 1), amount=D("600"), payment_method="CASH",
                            allocations=[{"document": inv.pk, "amount": D("600")}], user=accountant)
    row = calculate_customer_aging(AS_OF)["rows"][0]
    assert row["days_31_60"] == D("400.00") and row["total"] == D("400.00")


def test_aging_as_of_historical_date(accountant, customer, zero_tax_product, make_sale):
    inv = invoice(make_sale, customer, zero_tax_product, 1000, dt.date(2026, 7, 1))  # due 07-31
    create_customer_receipt(customer_id=customer.pk, date=dt.date(2026, 9, 1), amount=D("1000"), payment_method="BANK",
                            allocations=[{"document": inv.pk, "amount": D("1000")}], user=accountant)
    invoice(make_sale, customer, zero_tax_product, 50, dt.date(2026, 9, 20))
    # Today: first invoice paid, only the 50 is open and not yet due
    now = calculate_customer_aging(AS_OF)["totals"]
    assert (now["total"], now["current"]) == (D("50.00"), D("50.00"))
    # As of 2026-08-15 the 1,000 invoice was 15 days overdue and the 50 did not exist yet
    then = calculate_customer_aging(dt.date(2026, 8, 15))["totals"]
    assert (then["total"], then["days_1_30"]) == (D("1000.00"), D("1000.00"))
    # Before the invoice date nothing was outstanding
    assert calculate_customer_aging(dt.date(2026, 6, 30))["rows"] == []


def test_aging_historical_ignores_later_cancellation(api, customer, zero_tax_product, make_sale):
    sale = invoice(make_sale, customer, zero_tax_product, 700, dt.date(2026, 8, 1))
    api.post(f"/api/sales/{sale.pk}/cancel/", {"reason": "x"}, format="json")
    assert calculate_customer_aging(timezone.localdate())["rows"] == []
    assert calculate_customer_aging(dt.date(2026, 8, 20))["totals"]["total"] == D("700.00")


def test_party_aging_detail_endpoint(api, customer, zero_tax_product, make_sale):
    invoice(make_sale, customer, zero_tax_product, 100, dt.date(2026, 6, 1))
    r = api.get(f"/api/customers/{customer.pk}/aging/?as_of=2026-09-27").json()["data"]
    doc = r["documents"][0]
    assert doc["days_overdue"] == 88 and doc["bucket"] == "days_61_90"
    detail = customer_aging_detail(customer.pk, AS_OF)
    assert detail["totals"]["days_61_90"] == D("100.00")


def test_customer_summary(api, accountant, customer, zero_tax_product, make_sale):
    invoice(make_sale, customer, zero_tax_product, 1000, dt.date(2026, 7, 1))
    invoice(make_sale, customer, zero_tax_product, 500, timezone.localdate())
    create_customer_receipt(customer_id=customer.pk, date=dt.date(2026, 8, 1), amount=D("200"), payment_method="CASH", user=accountant)
    s = api.get(f"/api/customers/{customer.pk}/summary/").json()["data"]
    assert s["total_documents"] == "1500.00"
    assert s["total_payments"] == "200.00"
    assert s["outstanding"] == "1500.00"
    assert s["unallocated"] == "200.00"
    assert s["net_balance"] == "1300.00"
    assert s["overdue"] == "1000.00"
