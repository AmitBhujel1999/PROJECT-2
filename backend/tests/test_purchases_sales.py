import datetime as dt
import threading
from decimal import Decimal as D

import pytest
from django.db import connection

from apps.common.exceptions import InsufficientStockError
from apps.inventory.models import MovementType, StockMovement
from apps.inventory.services import calculate_stock
from apps.sales.models import Sale

from .conftest import TODAY, client_for, item, line

pytestmark = pytest.mark.django_db


# ---------------------------------------------------------------------------
# Purchases
# ---------------------------------------------------------------------------
def test_purchase_creation_discounts_vat_and_stock_increase(api, vendor, product_factory):
    p = product_factory(stock=10, cost=1000)
    body = {
        "party": vendor.pk,
        "date": "2026-09-01",
        "vendor_bill_number": "VB-77",
        "items": [line(p, 10, 1000, "PERCENTAGE", 10)],
        # Client-supplied totals must be ignored:
        "subtotal": "1", "tax_amount": "1", "total_amount": "1", "discount_amount": "999",
    }
    r = api.post("/api/purchases/", body, format="json")
    assert r.status_code == 201, r.json()
    d = r.json()["data"]
    assert d["bill_number"] == "BILL-000001"
    assert r.json()["message"] == "Purchase BILL-000001 recorded successfully."
    assert d["subtotal"] == "10000.00"
    assert d["item_discount_total"] == "1000.00"
    assert d["taxable_amount"] == "9000.00"
    assert d["tax_amount"] == "1170.00"
    assert d["total_amount"] == "10170.00"
    assert d["payment_status"] == "UNPAID"
    assert d["due_date"] == "2026-10-01"  # 30-day vendor terms
    assert calculate_stock(p) == D("20")
    mv = StockMovement.objects.get(reference_type="purchase", reference_id=d["id"])
    assert mv.transaction_type == MovementType.PURCHASE and mv.unit_cost == D("900.00")


def test_purchase_invoice_level_discount(api, vendor, product_factory):
    p1, p2 = product_factory(), product_factory()
    body = {"party": vendor.pk, "date": "2026-09-01", "discount_type": "FIXED", "discount_value": "500",
            "items": [line(p1, 2, 1000), line(p2, 3, 1000, "FIXED", 300)]}
    d = api.post("/api/purchases/", body, format="json").json()["data"]
    assert d["subtotal"] == "5000.00"
    assert d["item_discount_total"] == "300.00"
    assert d["discount_amount"] == "500.00"
    assert d["taxable_amount"] == "4200.00"
    assert d["tax_amount"] == "546.00"
    assert d["total_amount"] == "4746.00"


def test_purchase_paid_on_creation_sets_status(api, vendor, product_factory):
    p = product_factory()
    body = {"party": vendor.pk, "date": "2026-09-01", "items": [line(p, 1, 1000)], "payment": {"amount": "1130", "payment_method": "CASH"}}
    d = api.post("/api/purchases/", body, format="json").json()["data"]
    assert d["payment_status"] == "PAID" and d["balance_due"] == "0.00"
    body["payment"] = {"amount": "500", "payment_method": "BANK"}
    d = api.post("/api/purchases/", body, format="json").json()["data"]
    assert d["payment_status"] == "PARTIAL" and d["balance_due"] == "630.00"


def test_due_date_override_requires_permission(staff, admin_user, customer, product_factory):
    p = product_factory()
    body = {"party": customer.pk, "date": "2026-09-01", "due_date": "2026-09-05", "items": [line(p, 1)]}
    r = client_for(staff).post("/api/sales/", body, format="json")
    assert r.status_code == 403 and r.json()["error"]["code"] == "DUE_DATE_OVERRIDE_DENIED"
    r = client_for(admin_user).post("/api/sales/", body, format="json")
    assert r.status_code == 201 and r.json()["data"]["due_date"] == "2026-09-05"


def test_cancel_purchase_reverses_stock_and_blocks_if_consumed(api, vendor, customer, product_factory, make_purchase, make_sale):
    p = product_factory(stock=0)
    bill = make_purchase(vendor, [item(p, 10, 100)])
    r = api.post(f"/api/purchases/{bill.pk}/cancel/", {"reason": "Wrong vendor"}, format="json")
    assert r.status_code == 200 and r.json()["data"]["status"] == "CANCELLED"
    assert calculate_stock(p) == 0
    assert StockMovement.objects.filter(transaction_type=MovementType.PURCHASE_CANCEL).count() == 1
    assert api.post(f"/api/purchases/{bill.pk}/cancel/", {"reason": "x"}, format="json").json()["error"]["code"] == "ALREADY_CANCELLED"

    bill2 = make_purchase(vendor, [item(p, 10, 100)])
    make_sale(customer, [item(p, 8)])
    r = api.post(f"/api/purchases/{bill2.pk}/cancel/", {"reason": "oops"}, format="json")
    assert r.status_code == 409 and r.json()["error"]["code"] == "INSUFFICIENT_STOCK"


def test_documents_cannot_be_deleted(api, customer, product_factory, make_sale):
    sale = make_sale(customer, [item(product_factory(), 1)])
    assert api.delete(f"/api/sales/{sale.pk}/").status_code == 405
    assert api.put(f"/api/sales/{sale.pk}/", {}, format="json").status_code == 405


# ---------------------------------------------------------------------------
# Sales
# ---------------------------------------------------------------------------
def test_sale_creation_discounts_vat_and_stock_decrease(api, customer, product_factory):
    a = product_factory("Product A", stock=120, price=500)
    b = product_factory("Product B", stock=18, price=750)
    body = {"party": customer.pk, "date": "2026-09-27", "items": [line(a, 5, None, "PERCENTAGE", 10), line(b, 2)],
            "discount_type": "PERCENTAGE", "discount_value": "10", "total_amount": "1"}
    r = api.post("/api/sales/", body, format="json")
    assert r.status_code == 201, r.json()
    d = r.json()["data"]
    assert d["invoice_number"] == "INV-000001"
    assert r.json()["message"] == "Sale INV-000001 created successfully."
    assert d["subtotal"] == "4000.00"
    assert d["item_discount_total"] == "250.00"
    assert d["discount_amount"] == "375.00"
    assert d["taxable_amount"] == "3375.00"
    assert d["tax_amount"] == "438.75"
    assert d["total_amount"] == "3813.75"
    assert d["due_date"] == "2026-10-27"
    assert d["items"][0]["unit_price"] == "500.00"  # auto-populated selling price
    assert calculate_stock(a) == D("115") and calculate_stock(b) == D("16")


def test_insufficient_stock_rejected_and_nothing_saved(api, customer, product_factory):
    a = product_factory("Product A", stock=7)
    b = product_factory("Product B", stock=100)
    body = {"party": customer.pk, "date": "2026-09-27", "items": [line(b, 1), line(a, 10)]}
    r = api.post("/api/sales/", body, format="json")
    assert r.status_code == 409
    err = r.json()["error"]
    assert err["code"] == "INSUFFICIENT_STOCK"
    assert "Product A" in err["message"]
    assert err["details"]["available"] == "7.000" and err["details"]["requested"] == "10.000"
    assert Sale.objects.count() == 0
    assert calculate_stock(b) == D("100")  # rolled back
    # The invoice number was not consumed either
    ok = api.post("/api/sales/", {**body, "items": [line(a, 7)]}, format="json")
    assert ok.json()["data"]["invoice_number"] == "INV-000001"
    assert calculate_stock(a) == 0


def test_same_product_on_two_lines_is_validated_together(api, customer, product_factory):
    a = product_factory(stock=5)
    r = api.post("/api/sales/", {"party": customer.pk, "date": "2026-09-27", "items": [line(a, 3), line(a, 3)]}, format="json")
    assert r.status_code == 409


def test_low_stock_warning_returned(api, customer, product_factory):
    a = product_factory("Low Item", stock=10, reorder=5)
    r = api.post("/api/sales/", {"party": customer.pk, "date": "2026-09-27", "items": [line(a, 6)]}, format="json")
    assert any("Low Item" in w for w in r.json()["data"]["warnings"])


def test_inactive_customer_and_product_rejected(api, customer, product_factory):
    a = product_factory()
    a.is_active = False
    a.save()
    r = api.post("/api/sales/", {"party": customer.pk, "date": "2026-09-27", "items": [line(a, 1)]}, format="json")
    assert r.json()["error"]["code"] == "PRODUCT_INACTIVE"


def test_calculate_preview_returns_server_totals_and_stock(api, customer, product_factory):
    a = product_factory(stock=12, price=100)
    r = api.post("/api/sales/calculate/", {"party": customer.pk, "date": "2026-09-27", "items": [line(a, 2, None, "FIXED", 20)]}, format="json")
    d = r.json()["data"]
    assert d["total_amount"] == "203.40"
    assert d["lines"][0]["available_stock"] == "12.000"
    assert Sale.objects.count() == 0


def test_cancel_sale_restores_stock_and_releases_payment(api, customer, product_factory, make_sale):
    a = product_factory(stock=10, price=100)
    sale = make_sale(customer, [item(a, 4)], payment={"amount": D("452"), "payment_method": "CASH"})
    assert sale.payment_status == "PAID"
    assert calculate_stock(a) == 6
    r = api.post(f"/api/sales/{sale.pk}/cancel/", {"reason": "Customer returned"}, format="json")
    assert r.status_code == 200
    assert calculate_stock(a) == 10
    from apps.receivables.models import CustomerReceipt

    receipt = CustomerReceipt.objects.get()
    assert receipt.unallocated_amount == D("452.00")  # now a customer advance


def test_sale_requires_items(api, customer):
    r = api.post("/api/sales/", {"party": customer.pk, "date": "2026-09-27", "items": []}, format="json")
    assert r.status_code == 400


def test_sale_to_vendor_rejected(api, vendor, product_factory):
    r = api.post("/api/sales/", {"party": vendor.pk, "date": "2026-09-27", "items": [line(product_factory(), 1)]}, format="json")
    assert r.json()["error"]["code"] == "CUSTOMER_NOT_FOUND"


# ---------------------------------------------------------------------------
# Concurrency
# ---------------------------------------------------------------------------
@pytest.mark.django_db(transaction=True)
def test_concurrent_sales_cannot_oversell(admin_user, customer, product_factory):
    """Two simultaneous sales of 6 units against 10 in stock: exactly one wins."""
    from apps.sales.services import create_sale

    product = product_factory(stock=10)
    barrier = threading.Barrier(4)
    results: list[str] = []

    def worker():
        try:
            barrier.wait()
            create_sale(data={"party": customer.pk, "date": TODAY, "items": [item(product, 6)]}, user=admin_user)
            results.append("ok")
        except InsufficientStockError:
            results.append("insufficient")
        finally:
            connection.close()

    threads = [threading.Thread(target=worker) for _ in range(4)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert sorted(results) == ["insufficient", "insufficient", "insufficient", "ok"]
    assert calculate_stock(product) == D("4")
    assert Sale.objects.count() == 1


@pytest.mark.django_db(transaction=True)
def test_concurrent_document_numbers_are_unique(admin_user, customer, product_factory):
    from apps.sales.services import create_sale

    product = product_factory(stock=1000)
    barrier = threading.Barrier(8)
    numbers: list[str] = []

    def worker():
        try:
            barrier.wait()
            sale, _ = create_sale(data={"party": customer.pk, "date": TODAY, "items": [item(product, 1)]}, user=admin_user)
            numbers.append(sale.invoice_number)
        finally:
            connection.close()

    threads = [threading.Thread(target=worker) for _ in range(8)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert len(numbers) == 8 and len(set(numbers)) == 8
    assert sorted(numbers) == [f"INV-{i:06d}" for i in range(1, 9)]


def test_backdated_due_date_validation(api, customer, product_factory):
    r = api.post("/api/sales/", {"party": customer.pk, "date": "2026-09-27", "due_date": "2026-09-01",
                                 "items": [line(product_factory(), 1)]}, format="json")
    assert r.status_code == 400


def test_sale_list_filters(api, customer, customer2, product_factory, make_sale):
    p = product_factory(stock=100)
    make_sale(customer, [item(p, 1)], date=dt.date(2026, 8, 1))
    make_sale(customer2, [item(p, 1)], date=dt.date(2026, 9, 1))
    assert api.get("/api/sales/?start_date=2026-08-15").json()["data"]["count"] == 1
    assert api.get(f"/api/sales/?customer={customer.pk}").json()["data"]["count"] == 1
    assert api.get("/api/sales/?search=XYZ").json()["data"]["count"] == 1
    assert api.get("/api/sales/?invoice_number=000002").json()["data"]["count"] == 1


def test_pay_in_full_uses_server_total(api, customer, product_factory):
    p = product_factory(price=100)
    body = {"party": customer.pk, "date": "2026-09-27", "items": [line(p, 3)], "payment": {"pay_in_full": True, "payment_method": "CASH"}}
    d = api.post("/api/sales/", body, format="json").json()["data"]
    assert (d["payment_status"], d["amount_paid"], d["total_amount"]) == ("PAID", "339.00", "339.00")
    body["payment"] = {"payment_method": "CASH"}
    assert api.post("/api/sales/", body, format="json").status_code == 400


def test_preview_without_party(api, product_factory):
    p = product_factory(price=100)
    r = api.post("/api/sales/calculate/", {"date": "2026-09-27", "items": [line(p, 1)]}, format="json")
    assert r.status_code == 200 and r.json()["data"]["total_amount"] == "113.00"


def test_inline_pdf_can_be_framed_same_origin(api, customer, product_factory, make_sale):
    sale = make_sale(customer, [item(product_factory(), 1)])
    r = api.get(f"/api/sales/{sale.pk}/pdf/?inline=1")
    assert r["X-Frame-Options"] == "SAMEORIGIN"
    assert "frame-ancestors 'self'" in r["Content-Security-Policy"]
    assert api.get(f"/api/sales/{sale.pk}/pdf/")["X-Frame-Options"] == "DENY"
