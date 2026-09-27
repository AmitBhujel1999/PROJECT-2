import datetime as dt
from decimal import Decimal

import pytest
from rest_framework.test import APIClient

from apps.parties.models import CreditTerms, Party, PartyType
from apps.products.services import create_product
from apps.users.models import Role, User

D = Decimal
TODAY = dt.date(2026, 9, 27)


@pytest.fixture(autouse=True)
def _fixed_settings(settings):
    settings.ALLOWED_HOSTS = ["testserver", "localhost"]
    settings.REST_FRAMEWORK = {
        **settings.REST_FRAMEWORK,
        "DEFAULT_THROTTLE_RATES": {"anon": "10000/minute", "user": "10000/minute", "login": "10000/minute", "password_reset": "10000/minute"},
    }


def make_user(role: str, username: str | None = None) -> User:
    username = username or f"user_{role.lower()}"
    user = User.objects.create_user(username=username, email=f"{username}@example.com", password="Sup3r-Secret!", role=role)
    return user


@pytest.fixture
def admin_user(db):
    return make_user(Role.ADMIN, "admin1")


@pytest.fixture
def manager(db):
    return make_user(Role.MANAGER)


@pytest.fixture
def accountant(db):
    return make_user(Role.ACCOUNTANT)


@pytest.fixture
def staff(db):
    return make_user(Role.STAFF)


def client_for(user) -> APIClient:
    client = APIClient()
    client.force_authenticate(user=None)
    client.force_login(user)
    return client


@pytest.fixture
def api(admin_user) -> APIClient:
    return client_for(admin_user)


@pytest.fixture
def anon() -> APIClient:
    return APIClient()


@pytest.fixture
def product_factory(admin_user):
    counter = {"n": 0}

    def make(name=None, *, stock=D("100"), cost=D("1000"), price=D("2000"), tax=D("13"), reorder=D("5"), unit="PCS", opening_date=None):
        counter["n"] += 1
        return create_product(
            data={
                "name": name or f"Product {counter['n']}",
                "sku_code": f"SKU-{counter['n']:04d}",
                "unit": unit,
                "opening_stock": D(stock),
                "purchase_price": D(cost),
                "selling_price": D(price),
                "tax_rate": D(tax),
                "reorder_level": D(reorder),
            },
            user=admin_user,
            opening_date=opening_date or dt.date(2026, 1, 1),
        )

    return make


@pytest.fixture
def customer(db):
    return Party.objects.create(type=PartyType.CUSTOMER, name="ABC Traders", credit_terms=CreditTerms.DAYS_30, pan_vat_no="301234567")


@pytest.fixture
def customer2(db):
    return Party.objects.create(type=PartyType.CUSTOMER, name="XYZ Store", credit_terms=CreditTerms.DAYS_15)


@pytest.fixture
def vendor(db):
    return Party.objects.create(type=PartyType.VENDOR, name="ABC Suppliers", credit_terms=CreditTerms.DAYS_30)


def sale_payload(customer, items, date=TODAY, **extra):
    return {"party": customer.pk, "date": date.isoformat(), "items": items, **extra}


def line(product, qty, price=None, dtype=None, dval="0"):
    data = {"product": product.pk, "quantity": str(qty), "discount_type": dtype, "discount_value": str(dval)}
    if price is not None:
        data["unit_price"] = str(price)
    return data


@pytest.fixture
def make_sale(admin_user):
    from apps.sales.services import create_sale

    def make(customer, items, date=TODAY, **extra):
        data = {"party": customer.pk, "date": date, "items": items, **extra}
        sale, _ = create_sale(data=data, user=admin_user)
        return sale

    return make


@pytest.fixture
def make_purchase(admin_user):
    from apps.purchases.services import create_purchase

    def make(vendor, items, date=TODAY, **extra):
        return create_purchase(data={"party": vendor.pk, "date": date, "items": items, **extra}, user=admin_user)

    return make


def item(product, qty, price=None, dtype=None, dval=0):
    data = {"product": product.pk, "quantity": D(str(qty)), "discount_type": dtype, "discount_value": D(str(dval))}
    if price is not None:
        data["unit_price"] = D(str(price))
    return data
