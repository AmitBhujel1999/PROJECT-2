import datetime as dt
from decimal import Decimal as D

import pytest
from django.db import connection, transaction
from django.db.utils import DatabaseError, IntegrityError

from apps.audit.models import AuditLog
from apps.inventory.models import StockMovement
from apps.inventory.services import calculate_historical_stock, calculate_stock, calculate_stock_between

from .conftest import client_for, item

pytestmark = pytest.mark.django_db


def test_historical_stock_and_between_dates(customer, vendor, product_factory, make_purchase, make_sale):
    p = product_factory(stock=10, opening_date=dt.date(2026, 1, 1))
    make_purchase(vendor, [item(p, 20, 100)], date=dt.date(2026, 3, 1))
    make_sale(customer, [item(p, 5)], date=dt.date(2026, 4, 1))
    make_sale(customer, [item(p, 7)], date=dt.date(2026, 5, 1))
    assert calculate_stock(p) == D("18")
    assert calculate_historical_stock(p, dt.date(2025, 12, 31)) == D("0")
    assert calculate_historical_stock(p, dt.date(2026, 2, 1)) == D("10")
    assert calculate_historical_stock(p, dt.date(2026, 3, 1)) == D("30")
    assert calculate_historical_stock(p, dt.date(2026, 4, 15)) == D("25")
    period = calculate_stock_between(p, dt.date(2026, 3, 15), dt.date(2026, 4, 30))
    assert period == {"opening": D("30"), "quantity_in": D("0"), "quantity_out": D("5"), "closing": D("25")}


def test_product_stock_endpoint(api, product_factory):
    p = product_factory(stock=10, opening_date=dt.date(2026, 1, 1))
    d = api.get(f"/api/inventory/{p.pk}/?as_of=2025-06-01&start_date=2026-01-01&end_date=2026-12-31").json()["data"]
    assert d["current_stock"] == "10.000"
    assert d["stock_as_of"] == "0.000"
    assert d["period"]["closing"] == "10.000"


def test_stock_adjustment_creates_ledger_and_audit(api, product_factory):
    p = product_factory(stock=20)
    r = api.post("/api/inventory/adjustments/", {"product": p.pk, "date": "2026-09-27", "quantity": "-2", "reason": "DAMAGED",
                                                  "notes": "Damaged goods"}, format="json")
    assert r.status_code == 201, r.json()
    d = r.json()["data"]
    assert d["adjustment_number"] == "ADJ-000001"
    assert (d["stock_before"], d["stock_after"]) == ("20.000", "18.000")
    assert calculate_stock(p) == D("18")
    mv = StockMovement.objects.filter(product=p).last()
    assert mv.transaction_type == "ADJUSTMENT" and mv.quantity_out == D("2")
    assert AuditLog.objects.filter(model_name="inventory.StockAdjustment", action="CREATE").exists()


def test_negative_adjustment_cannot_make_stock_negative(api, product_factory):
    p = product_factory(stock=1)
    r = api.post("/api/inventory/adjustments/", {"product": p.pk, "date": "2026-09-27", "quantity": "-5", "reason": "LOST"}, format="json")
    assert r.status_code == 409 and r.json()["error"]["code"] == "INSUFFICIENT_STOCK"


def test_adjustment_other_reason_requires_notes_and_zero_rejected(api, product_factory):
    p = product_factory()
    assert api.post("/api/inventory/adjustments/", {"product": p.pk, "date": "2026-09-27", "quantity": "1", "reason": "OTHER"}, format="json").status_code == 400
    assert api.post("/api/inventory/adjustments/", {"product": p.pk, "date": "2026-09-27", "quantity": "0", "reason": "FOUND"}, format="json").status_code == 400


def test_only_managers_can_adjust(accountant, staff, product_factory):
    p = product_factory()
    body = {"product": p.pk, "date": "2026-09-27", "quantity": "1", "reason": "FOUND"}
    assert client_for(accountant).post("/api/inventory/adjustments/", body, format="json").status_code == 403
    assert client_for(staff).post("/api/inventory/adjustments/", body, format="json").status_code == 403
    assert client_for(staff).get("/api/inventory/adjustments/").status_code == 200


def test_stock_ledger_is_immutable(product_factory):
    p = product_factory(stock=5)
    mv = StockMovement.objects.get(product=p)
    with pytest.raises(RuntimeError):
        mv.save()
    with pytest.raises(RuntimeError):
        mv.delete()
    with pytest.raises(RuntimeError):
        StockMovement.objects.filter(pk=mv.pk).update(quantity_in=1)
    with pytest.raises(DatabaseError), transaction.atomic(), connection.cursor() as cur:
        cur.execute("UPDATE inventory_stockmovement SET quantity_in = 99 WHERE id = %s", [mv.pk])
    with pytest.raises(DatabaseError), transaction.atomic(), connection.cursor() as cur:
        cur.execute("DELETE FROM inventory_stockmovement WHERE id = %s", [mv.pk])


def test_database_rejects_negative_running_balance(product_factory):
    p = product_factory(stock=0)
    with pytest.raises(IntegrityError), transaction.atomic():
        StockMovement.objects.create(product=p, date=dt.date(2026, 1, 1), transaction_type="SALE", quantity_out=1, running_balance=-1)


def test_stock_ledger_api_opening_closing(api, customer, vendor, product_factory, make_purchase, make_sale):
    p = product_factory(stock=10, opening_date=dt.date(2026, 1, 1))
    make_purchase(vendor, [item(p, 5, 100)], date=dt.date(2026, 2, 1))
    make_sale(customer, [item(p, 3)], date=dt.date(2026, 3, 1))
    rows = api.get(f"/api/inventory/ledger/?product={p.pk}&start_date=2026-02-01").json()["data"]["results"]
    assert [(r["transaction_type"], r["opening_quantity"], r["quantity_in"], r["quantity_out"], r["closing_quantity"]) for r in rows] == [
        ("PURCHASE", "10.000", "5.000", "0.000", "15.000"),
        ("SALE", "15.000", "0.000", "3.000", "12.000"),
    ]
    rows = api.get(f"/api/inventory/ledger/?product={p.pk}&transaction_type=SALE").json()["data"]["results"]
    assert len(rows) == 1 and rows[0]["closing_quantity"] == "12.000"


def test_stock_register_status_and_values(api, customer, product_factory, make_sale):
    in_stock = product_factory("In", stock=50, reorder=5, cost=10, price=15)
    low = product_factory("Low", stock=4, reorder=5)
    out = product_factory("Out", stock=2, reorder=1)
    make_sale(customer, [item(out, 2)])
    data = api.get("/api/inventory/").json()["data"]
    rows = {r["name"]: r for r in data["results"]}
    assert rows["In"]["stock_status"] == "IN_STOCK"
    assert rows["In"]["cost_value"] == "500.00" and rows["In"]["retail_value"] == "750.00"
    assert rows["Low"]["stock_status"] == "LOW_STOCK"
    assert rows["Out"]["stock_status"] == "OUT_OF_STOCK" and rows["Out"]["total_sold"] == "2.000"
    assert data["summary"]["low_stock_count"] == 1 and data["summary"]["out_of_stock_count"] == 1
    assert api.get("/api/inventory/?status=LOW_STOCK").json()["data"]["count"] == 1
    assert low.pk and in_stock.pk


def test_stock_register_as_of(api, customer, product_factory, make_sale):
    p = product_factory(stock=10, opening_date=dt.date(2026, 1, 1))
    make_sale(customer, [item(p, 4)], date=dt.date(2026, 6, 1))
    row = api.get("/api/inventory/?as_of=2026-05-31").json()["data"]["results"][0]
    assert row["current_stock"] == "10.000"
    row = api.get("/api/inventory/?as_of=2026-06-01").json()["data"]["results"][0]
    assert row["current_stock"] == "6.000"
