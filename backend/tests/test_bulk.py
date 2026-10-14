from decimal import Decimal as D

from apps.parties.models import Party, PartyType
from apps.products.models import Product

from .conftest import client_for, make_user


def test_bulk_products_create_and_update(api, product_factory):
    p = product_factory(name="Rice", cost=D("100"), price=D("120"))
    rows = [
        {"id": p.pk, "selling_price": "130.00", "tax_rate": "0"},
        {"name": "Dal", "sku_code": "dal-1", "unit": "KG", "purchase_price": "140", "selling_price": "165"},
        {"name": "Oil", "sku_code": "OIL-1", "unit": "LTR", "is_active": False},
    ]
    r = api.post("/api/products/bulk/", {"rows": rows}, format="json")
    assert r.status_code == 200, r.content
    assert r.json()["data"] == {"created": 2, "updated": 1}
    p.refresh_from_db()
    assert p.selling_price == D("130.00") and p.tax_rate == D("0")
    assert Product.objects.get(sku_code="DAL-1").selling_price == D("165")
    assert Product.objects.get(sku_code="OIL-1").is_active is False
    # Unchanged rows are not counted.
    assert api.post("/api/products/bulk/", {"rows": [{"id": p.pk, "selling_price": "130"}]}, format="json").json()["data"]["updated"] == 0


def test_bulk_products_all_or_nothing(api, product_factory):
    p = product_factory(name="Rice")
    rows = [
        {"id": p.pk, "selling_price": "999"},
        {"name": "A", "sku_code": "DUP"},
        {"name": "B", "sku_code": "dup"},
        {"name": "", "sku_code": "X1", "selling_price": "-5"},
    ]
    r = api.post("/api/products/bulk/", {"rows": rows}, format="json")
    assert r.status_code == 400
    errors = r.json()["error"]["details"]["rows"]
    assert set(errors) == {"2", "3"}
    assert "sku_code" in errors["2"] and {"name", "selling_price"} <= set(errors["3"])
    p.refresh_from_db()
    assert p.selling_price != D("999")
    assert not Product.objects.filter(sku_code="DUP").exists()


def test_bulk_customers(api, customer):
    rows = [
        {"id": customer.pk, "phone": "9800000000", "credit_terms": "DAYS_45", "credit_limit": "50000"},
        {"name": "New Shop", "phone": "9811111111", "credit_terms": "CASH"},
    ]
    r = api.post("/api/customers/bulk/", {"rows": rows}, format="json")
    assert r.status_code == 200, r.content
    customer.refresh_from_db()
    assert customer.credit_days == 45 and customer.credit_limit == D("50000") and customer.phone == "9800000000"
    assert Party.objects.get(name="New Shop").type == PartyType.CUSTOMER


def test_bulk_vendors_cannot_touch_customers(api, customer):
    r = api.post("/api/vendors/bulk/", {"rows": [{"id": customer.pk, "name": "Hijack"}]}, format="json")
    assert r.status_code == 400
    customer.refresh_from_db()
    assert customer.name != "Hijack"


def test_bulk_needs_manage_permission(db, customer):
    from apps.users.models import Role

    staff = client_for(make_user(Role.STAFF, "bulk_staff"))
    assert staff.post("/api/customers/bulk/", {"rows": [{"name": "X"}]}, format="json").status_code == 403
    assert staff.post("/api/products/bulk/", {"rows": [{"name": "X", "sku_code": "X"}]}, format="json").status_code == 403
