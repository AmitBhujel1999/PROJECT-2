import pytest
from django.db import connection

from apps.common import companies
from apps.products.models import Product
from apps.users.models import Role

from .conftest import make_user

NEW_COMPANY = {
    "name": "Fresh Traders",
    "address": "Pokhara",
    "admin_username": "owner",
    "admin_password": "Fresh-Start-2026",
    "authorize_company": "main",
    "authorize_username": "admin1",
    "authorize_password": "Sup3r-Secret!",
}


def test_list_has_main_company(anon, db):
    res = anon.get("/api/companies/")
    assert res.status_code == 200
    assert res.json()["data"]["companies"][0]["slug"] == "main"


def test_create_requires_admin_of_existing_company(anon, db):
    make_user(Role.MANAGER, "admin1")
    res = anon.post("/api/companies/", NEW_COMPANY, format="json")
    assert res.status_code == 400
    assert res.json()["error"]["code"] == "INVALID_CREDENTIALS"


@pytest.mark.django_db(transaction=True)
def test_new_company_is_empty_and_separate(anon, product_factory):
    product_factory("Old item")
    try:
        res = anon.post("/api/companies/", NEW_COMPANY, format="json")
        assert res.status_code == 201, res.json()
        slug = res.json()["data"]["slug"]
        assert slug in [c["slug"] for c in anon.get("/api/companies/").json()["data"]["companies"]]

        # Main company's admin cannot log in to the new company.
        bad = anon.post("/api/auth/login/", {"username": "admin1", "password": "Sup3r-Secret!", "company": slug}, format="json")
        assert bad.status_code == 400

        login = anon.post(
            "/api/auth/login/", {"username": "owner", "password": "Fresh-Start-2026", "company": slug}, format="json"
        )
        assert login.status_code == 200
        assert anon.cookies[companies.COOKIE_NAME].value == slug
        assert anon.get("/api/products/").json()["data"]["count"] == 0
        assert anon.get("/api/settings/").json()["data"]["business_name"] == "Fresh Traders"
        assert Product.objects.filter(name="Old item").exists()  # test code still sees the main company
    finally:
        companies.activate(companies.MAIN_SCHEMA)
        with connection.cursor() as cursor:
            for _slug, _name, schema in companies._registry_rows():
                cursor.execute(f"DROP SCHEMA IF EXISTS {connection.ops.quote_name(schema)} CASCADE")
            cursor.execute("DELETE FROM public.acct_company")
