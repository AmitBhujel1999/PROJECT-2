from decimal import Decimal

import pytest
from django.db import IntegrityError, transaction
from django.utils import timezone

from apps.audit.models import AuditLog
from apps.expenses.models import Expense, ExpenseCategory
from apps.expenses.services import DEFAULT_CATEGORIES, calculate_expense, create_expense, ensure_default_categories

from .conftest import TODAY, client_for

D = Decimal
pytestmark = pytest.mark.django_db


@pytest.fixture(autouse=True)
def categories(db):
    # Migration-seeded rows can be flushed by transactional tests, so create them here.
    return ensure_default_categories()


@pytest.fixture
def rent(categories):
    return categories["Rent"]


def payload(category, **extra):
    return {"date": TODAY.isoformat(), "category": category.pk, "description": "September rent", "amount": "10000", **extra}


# ---------------------------------------------------------------------------
# Calculation
# ---------------------------------------------------------------------------
def test_calculate_expense_rounds_tax_half_up():
    assert calculate_expense(D("1000"), D("13")) == {
        "amount": D("1000.00"), "tax_rate": D("13.00"), "tax_amount": D("130.00"), "total_amount": D("1130.00"),
    }
    # 333.33 * 13% = 43.3329 -> 43.33
    assert calculate_expense(D("333.33"), D("13"))["tax_amount"] == D("43.33")
    # 0.05 * 13% = 0.0065 -> 0.01
    assert calculate_expense(D("0.05"), D("13"))["tax_amount"] == D("0.01")
    assert calculate_expense(D("500"), None)["total_amount"] == D("500.00")


def test_ensure_default_categories_is_idempotent():
    ensure_default_categories()
    assert ExpenseCategory.objects.count() == len(DEFAULT_CATEGORIES)


# ---------------------------------------------------------------------------
# Expenses API
# ---------------------------------------------------------------------------
def test_create_expense_calculates_on_server_and_numbers_sequentially(api, rent):
    res = api.post("/api/expenses/", payload(rent, tax_rate="13", total_amount="1", tax_amount="999"), format="json")
    assert res.status_code == 201, res.json()
    data = res.json()["data"]
    assert data["expense_number"] == "EXP-000001"
    assert data["amount"] == "10000.00"
    assert data["tax_amount"] == "1300.00"
    assert data["total_amount"] == "11300.00"
    assert data["category_name"] == "Rent"
    assert data["status"] == "ACTIVE"
    res2 = api.post("/api/expenses/", payload(rent), format="json")
    assert res2.json()["data"]["expense_number"] == "EXP-000002"
    assert AuditLog.objects.filter(model_name="expenses.Expense", action="CREATE").count() == 2


def test_expense_with_vendor_ignores_payee(api, rent, vendor):
    res = api.post("/api/expenses/", payload(rent, vendor=vendor.pk, payee="Someone else"), format="json")
    data = res.json()["data"]
    assert data["vendor_name"] == vendor.name
    assert data["payee"] == ""
    assert data["paid_to"] == vendor.name


def test_expense_with_payee(api, rent):
    data = api.post("/api/expenses/", payload(rent, payee="Mr. Landlord"), format="json").json()["data"]
    assert data["paid_to"] == "Mr. Landlord"


@pytest.mark.parametrize(
    "extra, field",
    [
        ({"amount": "0"}, "amount"),
        ({"amount": "-5"}, "amount"),
        ({"tax_rate": "101"}, "tax_rate"),
        ({"description": "   "}, "description"),
        ({"payment_method": "BITCOIN"}, "payment_method"),
    ],
)
def test_expense_validation(api, rent, extra, field):
    res = api.post("/api/expenses/", payload(rent, **extra), format="json")
    assert res.status_code == 400
    assert field in res.json()["error"]["details"]


def test_expense_rejects_inactive_category_and_customer_as_vendor(api, rent, customer):
    res = api.post("/api/expenses/", payload(rent, vendor=customer.pk), format="json")
    assert res.status_code == 400 and res.json()["error"]["code"] == "VENDOR_NOT_FOUND"
    rent.is_active = False
    rent.save()
    res = api.post("/api/expenses/", payload(rent), format="json")
    assert res.status_code == 400 and res.json()["error"]["code"] == "CATEGORY_INACTIVE"


def test_calculate_preview_saves_nothing(api):
    res = api.post("/api/expenses/calculate/", {"amount": "250", "tax_rate": "13"}, format="json")
    assert res.json()["data"] == {"amount": "250.00", "tax_rate": "13.00", "tax_amount": "32.50", "total_amount": "282.50"}
    assert not Expense.objects.exists()


def test_cancel_expense_keeps_record(api, rent):
    expense_id = api.post("/api/expenses/", payload(rent), format="json").json()["data"]["id"]
    res = api.post(f"/api/expenses/{expense_id}/cancel/", {"reason": "Entered twice"}, format="json")
    assert res.status_code == 200
    data = res.json()["data"]
    assert data["status"] == "CANCELLED" and data["cancel_reason"] == "Entered twice"
    again = api.post(f"/api/expenses/{expense_id}/cancel/", {"reason": "x"}, format="json")
    assert again.json()["error"]["code"] == "ALREADY_CANCELLED"
    assert api.delete(f"/api/expenses/{expense_id}/").status_code == 405
    assert api.patch(f"/api/expenses/{expense_id}/", {"amount": "1"}, format="json").status_code == 405


def test_list_filters(api, rent, vendor):
    utilities = ExpenseCategory.objects.get(name="Utilities")
    api.post("/api/expenses/", payload(rent, date="2026-09-01"), format="json")
    api.post("/api/expenses/", payload(utilities, description="Electricity bill", vendor=vendor.pk), format="json")
    data = api.get("/api/expenses/", {"category": utilities.pk}).json()["data"]
    assert [r["description"] for r in data["results"]] == ["Electricity bill"]
    assert api.get("/api/expenses/", {"start_date": "2026-09-10"}).json()["data"]["count"] == 1
    assert api.get("/api/expenses/", {"search": "electricity"}).json()["data"]["count"] == 1
    assert api.get("/api/expenses/", {"vendor": vendor.pk}).json()["data"]["count"] == 1


def test_expense_pdf(api, rent):
    expense_id = api.post("/api/expenses/", payload(rent, tax_rate="13"), format="json").json()["data"]["id"]
    res = api.get(f"/api/expenses/{expense_id}/pdf/")
    assert res.status_code == 200
    assert res["Content-Type"] == "application/pdf"
    assert res.content.startswith(b"%PDF")


def test_total_constraint_enforced_by_database(admin_user, rent):
    expense = create_expense(data={"date": TODAY, "category": rent.pk, "description": "x", "amount": D("100")}, user=admin_user)
    with pytest.raises(IntegrityError), transaction.atomic():
        Expense.objects.filter(pk=expense.pk).update(total_amount=D("1"))


# ---------------------------------------------------------------------------
# Permissions
# ---------------------------------------------------------------------------
def test_expense_permissions(rent, accountant, staff, manager):
    staff_api, acc_api, mgr_api = client_for(staff), client_for(accountant), client_for(manager)
    assert staff_api.get("/api/expenses/").status_code == 403
    assert staff_api.post("/api/expenses/", payload(rent), format="json").status_code == 403
    res = acc_api.post("/api/expenses/", payload(rent), format="json")
    assert res.status_code == 201
    expense_id = res.json()["data"]["id"]
    assert acc_api.post(f"/api/expenses/{expense_id}/cancel/", {"reason": "x"}, format="json").status_code == 403
    assert acc_api.post("/api/expense-categories/", {"name": "Fuel"}, format="json").status_code == 403
    assert mgr_api.post(f"/api/expenses/{expense_id}/cancel/", {"reason": "x"}, format="json").status_code == 200


# ---------------------------------------------------------------------------
# Categories
# ---------------------------------------------------------------------------
def test_category_crud(api, rent):
    res = api.post("/api/expense-categories/", {"name": "  Internet  ", "description": "ISP"}, format="json")
    assert res.status_code == 201
    cat = res.json()["data"]
    assert cat["name"] == "Internet" and cat["expense_count"] == 0
    dup = api.post("/api/expense-categories/", {"name": "internet"}, format="json")
    assert dup.status_code == 400 and dup.json()["error"]["code"] == "DUPLICATE_CATEGORY"
    assert api.patch(f"/api/expense-categories/{cat['id']}/", {"name": "RENT"}, format="json").json()["error"]["code"] == "DUPLICATE_CATEGORY"
    assert api.patch(f"/api/expense-categories/{cat['id']}/", {"name": "Internet & Phone"}, format="json").status_code == 200
    assert api.delete(f"/api/expense-categories/{cat['id']}/").status_code == 200

    api.post("/api/expenses/", payload(rent), format="json")
    res = api.delete(f"/api/expense-categories/{rent.pk}/")
    assert res.status_code == 409 and res.json()["error"]["code"] == "CATEGORY_HAS_EXPENSES"
    res = api.post(f"/api/expense-categories/{rent.pk}/deactivate/")
    assert res.json()["data"]["is_active"] is False
    assert api.get("/api/expense-categories/", {"is_active": "true"}).json()["data"]["count"] >= 10


# ---------------------------------------------------------------------------
# Report, dashboard and search
# ---------------------------------------------------------------------------
def test_expense_report_summary_and_breakdown(api, rent):
    utilities = ExpenseCategory.objects.get(name="Utilities")
    api.post("/api/expenses/", payload(rent, amount="10000"), format="json")
    api.post("/api/expenses/", payload(rent, amount="5000", tax_rate="13"), format="json")
    api.post("/api/expenses/", payload(utilities, amount="2000"), format="json")
    cancelled = api.post("/api/expenses/", payload(utilities, amount="999"), format="json").json()["data"]["id"]
    api.post(f"/api/expenses/{cancelled}/cancel/", {"reason": "mistake"}, format="json")

    data = api.get("/api/reports/expenses/").json()["data"]
    s = data["summary"]
    assert data["count"] == 3
    assert s["total_expenses"] == 3
    assert s["amount"] == "17000.00"
    assert s["tax_amount"] == "650.00"
    assert s["total_amount"] == "17650.00"
    assert [(c["category_name"], c["total_amount"]) for c in s["by_category"]] == [("Rent", "15650.00"), ("Utilities", "2000.00")]
    assert api.get("/api/reports/expenses/", {"status": "ALL"}).json()["data"]["count"] == 4

    csv = api.get("/api/reports/expenses/", {"export": "csv"})
    assert csv.status_code == 200 and b"September rent" in csv.content
    pdf = api.get("/api/reports/expenses/", {"export": "pdf"})
    assert pdf.content.startswith(b"%PDF")


def test_dashboard_and_search_include_expenses(api, rent, staff):
    today = timezone.localdate()
    api.post("/api/expenses/", payload(rent, date=today.isoformat(), amount="1234"), format="json")
    cards = api.get("/api/dashboard/").json()["data"]["cards"]
    assert cards["todays_expenses"] == "1234.00"
    assert cards["period_expenses"] == "1234.00"
    trend = api.get("/api/dashboard/").json()["data"]["trend"]
    assert sum(D(t["expenses"]) for t in trend) == D("1234.00")
    assert "todays_expenses" not in client_for(staff).get("/api/dashboard/").json()["data"]["cards"]

    groups = api.get("/api/search/", {"q": "september"}).json()["data"]["groups"]
    assert any(g["type"] == "expenses" for g in groups)
    groups = client_for(staff).get("/api/search/", {"q": "september"}).json()["data"]["groups"]
    assert not any(g["type"] == "expenses" for g in groups)
