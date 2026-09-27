from decimal import Decimal as D

import pytest

from apps.audit.models import AuditLog
from apps.inventory.models import MovementType, StockMovement
from apps.parties.models import Party

from .conftest import client_for

pytestmark = pytest.mark.django_db


PRODUCT = {
    "name": "Laptop",
    "sku_code": "lap-001",
    "unit": "PCS",
    "opening_stock": "20",
    "reorder_level": "5",
    "purchase_price": "50000",
    "selling_price": "65000",
}


def test_create_product_normalises_sku_and_posts_opening_stock(api):
    r = api.post("/api/products/", PRODUCT, format="json")
    assert r.status_code == 201, r.json()
    body = r.json()
    assert body["success"] is True
    assert body["data"]["sku_code"] == "LAP-001"
    assert body["data"]["tax_rate"] == "13.00"  # business default
    assert body["data"]["current_stock"] == "20.000"
    mv = StockMovement.objects.get(product_id=body["data"]["id"])
    assert mv.transaction_type == MovementType.OPENING_STOCK
    assert mv.quantity_in == D("20")
    assert AuditLog.objects.filter(model_name="products.Product", action="CREATE").exists()


def test_duplicate_sku_rejected_case_insensitive(api):
    assert api.post("/api/products/", PRODUCT, format="json").status_code == 201
    r = api.post("/api/products/", {**PRODUCT, "sku_code": "LAP-001", "name": "Other"}, format="json")
    assert r.status_code == 400
    assert r.json()["error"]["code"] == "VALIDATION_ERROR"
    assert "sku_code" in r.json()["error"]["details"]


def test_edit_product_and_opening_stock_is_immutable(api):
    pid = api.post("/api/products/", PRODUCT, format="json").json()["data"]["id"]
    r = api.patch(f"/api/products/{pid}/", {"selling_price": "70000", "name": "Laptop Pro"}, format="json")
    assert r.status_code == 200
    assert r.json()["data"]["selling_price"] == "70000.00"
    r = api.patch(f"/api/products/{pid}/", {"opening_stock": "50"}, format="json")
    assert r.status_code == 400
    audit = AuditLog.objects.filter(model_name="products.Product", action="UPDATE").first()
    assert audit.before_data["selling_price"] == "65000.00"
    assert audit.after_data["selling_price"] == "70000.00"


def test_negative_price_rejected(api):
    r = api.post("/api/products/", {**PRODUCT, "selling_price": "-1"}, format="json")
    assert r.status_code == 400


def test_deactivate_and_delete_protection(api):
    pid = api.post("/api/products/", PRODUCT, format="json").json()["data"]["id"]
    r = api.delete(f"/api/products/{pid}/")
    assert r.status_code == 409
    assert r.json()["error"]["code"] == "PRODUCT_HAS_HISTORY"
    r = api.post(f"/api/products/{pid}/deactivate/")
    assert r.json()["data"]["is_active"] is False
    assert api.get("/api/products/?is_active=true").json()["data"]["count"] == 0
    assert api.post(f"/api/products/{pid}/activate/").json()["data"]["is_active"] is True


def test_unused_product_can_be_deleted(api):
    pid = api.post("/api/products/", {**PRODUCT, "opening_stock": "0"}, format="json").json()["data"]["id"]
    assert api.delete(f"/api/products/{pid}/").status_code == 200


def test_product_search_and_pagination(api, product_factory):
    for i in range(30):
        product_factory(f"Widget {i}")
    product_factory("Special Gadget")
    r = api.get("/api/products/?search=gadget").json()["data"]
    assert r["count"] == 1
    r = api.get("/api/products/").json()["data"]
    assert (r["count"], len(r["results"]), r["page_size"], r["total_pages"]) == (31, 25, 25, 2)
    r = api.get("/api/products/?page_size=50").json()["data"]
    assert len(r["results"]) == 31
    r = api.get("/api/products/?page_size=7").json()["data"]
    assert r["page_size"] == 25  # only 25/50/100 allowed


def test_staff_cannot_manage_products(staff):
    c = client_for(staff)
    assert c.get("/api/products/").status_code == 200
    r = c.post("/api/products/", PRODUCT, format="json")
    assert r.status_code == 403
    assert r.json()["error"]["code"] == "PERMISSION_DENIED"


# ---------------------------------------------------------------------------
# Parties
# ---------------------------------------------------------------------------
def test_customer_quick_add_sets_type_and_credit_days(staff):
    c = client_for(staff)
    r = c.post("/api/customers/", {"name": "Quick Customer", "phone": "9800000000", "credit_terms": "DAYS_45"}, format="json")
    assert r.status_code == 201, r.json()
    d = r.json()["data"]
    assert d["type"] == "CUSTOMER"
    assert d["credit_days"] == 45


def test_custom_credit_terms(api):
    r = api.post("/api/vendors/", {"name": "V", "credit_terms": "CUSTOM", "credit_days": 21}, format="json")
    assert r.json()["data"]["credit_days"] == 21
    assert r.json()["data"]["type"] == "VENDOR"


def test_party_search_by_phone_and_pan(api):
    api.post("/api/customers/", {"name": "Alpha", "phone": "9811111111", "pan_vat_no": "123456789"}, format="json")
    api.post("/api/customers/", {"name": "Beta", "phone": "9822222222"}, format="json")
    assert api.get("/api/customers/?search=98111").json()["data"]["count"] == 1
    assert api.get("/api/customers/?search=123456789").json()["data"]["count"] == 1
    assert api.get("/api/vendors/").json()["data"]["count"] == 0


def test_party_type_cannot_change_and_staff_cannot_deactivate(api, staff, customer):
    r = api.patch(f"/api/customers/{customer.pk}/", {"type": "VENDOR", "name": "Renamed"}, format="json")
    assert r.status_code == 200
    customer.refresh_from_db()
    assert customer.type == "CUSTOMER" and customer.name == "Renamed"
    assert client_for(staff).post(f"/api/customers/{customer.pk}/deactivate/").status_code == 403
    assert api.post(f"/api/customers/{customer.pk}/deactivate/").json()["data"]["is_active"] is False


def test_parties_cannot_be_deleted(api, customer):
    assert api.delete(f"/api/customers/{customer.pk}/").status_code == 405
    assert Party.objects.filter(pk=customer.pk).exists()


def test_customer_endpoint_does_not_expose_vendors(api, customer, vendor):
    assert api.get(f"/api/customers/{vendor.pk}/").status_code == 404
