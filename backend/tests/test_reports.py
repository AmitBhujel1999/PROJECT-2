import datetime as dt
from decimal import Decimal as D

import pytest

from apps.purchases.models import Purchase
from apps.sales.models import Sale

from .conftest import client_for, item

pytestmark = pytest.mark.django_db


@pytest.fixture
def dataset(customer, customer2, vendor, product_factory, make_sale, make_purchase):
    p = product_factory("Report Item", stock=1000, price=100, cost=50)
    make_purchase(vendor, [item(p, 10, 50, "PERCENTAGE", 10)], date=dt.date(2026, 8, 1))
    make_purchase(vendor, [item(p, 10, 50)], date=dt.date(2026, 9, 1))
    make_sale(customer, [item(p, 2, None, "FIXED", 20)], date=dt.date(2026, 8, 5))
    make_sale(customer2, [item(p, 3)], date=dt.date(2026, 9, 5), discount_type="PERCENTAGE", discount_value=D("10"))
    make_sale(customer, [item(p, 1)], date=dt.date(2026, 9, 10))
    return p


def test_sales_report_summary_and_filters(api, dataset, customer):
    d = api.get("/api/reports/sales/").json()["data"]
    s = d["summary"]
    assert s["total_documents"] == 3
    assert s["gross_amount"] == "600.00"
    assert s["total_discount"] == "50.00"  # 20 item + 30 invoice
    assert s["taxable_amount"] == "550.00"
    assert s["tax_amount"] == "71.50"
    assert s["total_amount"] == "621.50"
    assert s["outstanding"] == "621.50"
    assert d["results"][0]["number"] == "INV-000003"  # newest first
    assert api.get("/api/reports/sales/?start_date=2026-09-01&end_date=2026-09-30").json()["data"]["count"] == 2
    assert api.get(f"/api/reports/sales/?party={customer.pk}").json()["data"]["count"] == 2
    assert api.get("/api/reports/sales/?number=000002").json()["data"]["count"] == 1
    assert api.get("/api/reports/sales/?search=xyz").json()["data"]["count"] == 1
    assert api.get("/api/reports/sales/?payment_status=PAID").json()["data"]["count"] == 0


def test_purchase_report(api, dataset):
    s = api.get("/api/reports/purchases/").json()["data"]["summary"]
    assert s["total_documents"] == 2
    assert s["gross_amount"] == "1000.00"
    assert s["total_discount"] == "50.00"
    assert s["tax_amount"] == "123.50"
    assert s["outstanding"] == "1073.50"


def test_report_pagination(api, customer, product_factory, make_sale):
    p = product_factory(stock=1000)
    for _ in range(30):
        make_sale(customer, [item(p, 1)])
    d = api.get("/api/reports/sales/?page=2").json()["data"]
    assert (d["count"], len(d["results"]), d["page"], d["total_pages"]) == (30, 5, 2, 2)
    assert len(api.get("/api/reports/sales/?page_size=100").json()["data"]["results"]) == 30


def test_csv_export_respects_filters(api, dataset):
    r = api.get("/api/reports/sales/?export=csv&start_date=2026-09-01")
    assert r.status_code == 200
    assert r["Content-Type"].startswith("text/csv")
    assert "attachment" in r["Content-Disposition"]
    text = r.content.decode("utf-8-sig")
    assert "INV-000002" in text and "INV-000003" in text and "INV-000001" not in text
    assert "Invoice #" in text and "TOTAL" in text


def test_pdf_exports(api, dataset, customer, vendor):
    urls = [
        "/api/reports/sales/?export=pdf",
        "/api/reports/purchases/?export=pdf",
        "/api/reports/stock/?export=pdf",
        "/api/reports/stock-ledger/?export=pdf",
        "/api/reports/receivables-aging/?export=pdf",
        "/api/reports/payables-aging/?export=pdf",
        f"/api/customers/{customer.pk}/ledger/?export=pdf",
        f"/api/vendors/{vendor.pk}/ledger/?export=pdf",
        f"/api/customers/{customer.pk}/statement/?export=pdf&start_date=2026-08-01",
        f"/api/vendors/{vendor.pk}/statement/?export=pdf&start_date=2026-08-01",
        f"/api/sales/{Sale.objects.first().pk}/pdf/",
        f"/api/purchases/{Purchase.objects.first().pk}/pdf/?inline=1",
    ]
    for url in urls:
        r = api.get(url)
        assert r.status_code == 200, url
        assert r["Content-Type"] == "application/pdf", url
        assert r.content.startswith(b"%PDF"), url


def test_statement_csv(api, dataset, customer):
    r = api.get(f"/api/customers/{customer.pk}/statement/?export=csv&start_date=2026-09-01&end_date=2026-09-30")
    text = r.content.decode("utf-8-sig")
    assert "Opening Balance" in text and "Closing Balance" in text and "INV-000003" in text


def test_csv_formula_injection_neutralised(api, product_factory, vendor):
    from apps.parties.models import Party

    Party.objects.filter(pk=vendor.pk).update(name="=HYPERLINK(\"http://evil\")")
    from apps.purchases.services import create_purchase
    from apps.users.models import User

    create_purchase(data={"party": vendor.pk, "date": dt.date(2026, 9, 1), "items": [item(product_factory(), 1, 10)]},
                    user=User.objects.get(username="admin1"))
    text = api.get("/api/reports/purchases/?export=csv").content.decode("utf-8-sig")
    assert "'=HYPERLINK" in text


def test_stock_report_and_aging_reports(api, dataset, customer):
    d = api.get("/api/reports/stock/").json()["data"]
    assert d["results"][0]["total_purchased"] == "20.000"
    assert d["results"][0]["total_sold"] == "6.000"
    aging = api.get("/api/reports/receivables-aging/?as_of=2026-09-27").json()["data"]
    assert aging["totals"]["total"] == "621.50"
    assert {r["party_name"] for r in aging["results"]} == {"ABC Traders", "XYZ Store"}
    assert api.get(f"/api/reports/receivables-aging/?party={customer.pk}").json()["data"]["count"] == 1
    payables = api.get("/api/reports/payables-aging/?as_of=2026-09-27").json()["data"]
    assert payables["totals"]["total"] == "1073.50"
    rec = api.get("/api/reports/receivables/?overdue=true&as_of=2026-09-27").json()["data"]
    # INV-1 (08-05, 30 days -> due 09-04) and INV-2 (09-05, 15 days -> due 09-20) are overdue
    assert rec["count"] == 2


def test_dashboard(api, dataset, staff):
    d = api.get("/api/dashboard/?start_date=2026-08-01&end_date=2026-09-30").json()["data"]
    assert d["cards"]["period_sales"] == "621.50"
    assert d["cards"]["receivables"] == "621.50"
    assert d["cards"]["total_customers"] == 2
    assert len(d["trend"]) == 61
    assert d["top_products"][0]["name"] == "Report Item"
    # Staff see the dashboard without financial receivable/payable figures
    staff_cards = client_for(staff).get("/api/dashboard/").json()["data"]["cards"]
    assert "receivables" not in staff_cards


def test_global_search(api, dataset, staff):
    d = api.get("/api/search/?q=abc").json()["data"]
    types = {g["type"] for g in d["groups"]}
    assert {"customers", "vendors", "invoices", "bills"} <= types
    d = api.get("/api/search/?q=INV-00000&type=invoices").json()["data"]
    assert d["count"] == 3
    # Staff cannot see receipts/payments groups
    d = client_for(staff).get("/api/search/?q=abc").json()["data"]
    assert "receipts" not in {g["type"] for g in d["groups"]}
    assert api.get("/api/search/?q=a").json()["data"]["groups"] == []


def test_reports_require_permission(staff):
    c = client_for(staff)
    assert c.get("/api/reports/sales/").status_code == 403
    assert c.get("/api/reports/receivables-aging/").status_code == 403
    assert c.get("/api/reports/stock/").status_code == 200
