import os
import re
import subprocess
import sys
from pathlib import Path

import pytest
from django.core import mail
from rest_framework.test import APIClient

from apps.audit.models import AuditLog
from apps.users.models import Role, User

from .conftest import client_for, make_user

pytestmark = pytest.mark.django_db
BACKEND = Path(__file__).resolve().parent.parent


def csrf_client():
    c = APIClient(enforce_csrf_checks=True)
    c.get("/api/auth/csrf/")
    return c, c.cookies["acct_csrftoken"].value


def test_login_logout_flow_with_csrf_and_http_only_cookie(staff):
    c, token = csrf_client()
    r = c.post("/api/auth/login/", {"username": staff.username, "password": "Sup3r-Secret!"}, format="json")
    assert r.status_code == 403 and r.json()["error"]["code"] == "CSRF_FAILED"
    r = c.post("/api/auth/login/", {"username": staff.username, "password": "Sup3r-Secret!"}, format="json", HTTP_X_CSRFTOKEN=token)
    assert r.status_code == 200
    assert r.json()["data"]["role"] == "STAFF"
    assert "sales.create" in r.json()["data"]["permissions"]
    assert r.cookies["acct_sessionid"]["httponly"]
    assert c.get("/api/auth/me/").json()["data"]["username"] == staff.username
    token = c.cookies["acct_csrftoken"].value
    # Authenticated unsafe requests require the CSRF token too
    assert c.post("/api/auth/logout/", format="json").status_code == 403
    assert c.post("/api/auth/logout/", format="json", HTTP_X_CSRFTOKEN=token).status_code == 200
    assert c.get("/api/auth/me/").status_code == 401
    actions = list(AuditLog.objects.filter(username=staff.username).values_list("action", flat=True))
    assert "LOGIN" in actions and "LOGOUT" in actions


def test_invalid_login(staff):
    c, token = csrf_client()
    r = c.post("/api/auth/login/", {"username": staff.username, "password": "wrong"}, format="json", HTTP_X_CSRFTOKEN=token)
    assert r.status_code == 400 and r.json()["error"]["code"] == "INVALID_CREDENTIALS"
    assert AuditLog.objects.filter(action="LOGIN_FAILED").exists()


def test_unauthenticated_requests_rejected(anon):
    for url in ["/api/products/", "/api/sales/", "/api/dashboard/", "/api/reports/sales/"]:
        r = anon.get(url)
        assert r.status_code == 401
        assert r.json() == {"success": False, "error": {"code": "AUTHENTICATION_REQUIRED", "message": "Authentication credentials were not provided."}}


def test_password_change(staff):
    c = client_for(staff)
    r = c.post("/api/auth/password/change/", {"current_password": "bad", "new_password": "An0ther-Secret!"}, format="json")
    assert r.status_code == 400
    r = c.post("/api/auth/password/change/", {"current_password": "Sup3r-Secret!", "new_password": "123"}, format="json")
    assert r.status_code == 400
    r = c.post("/api/auth/password/change/", {"current_password": "Sup3r-Secret!", "new_password": "An0ther-Secret!"}, format="json")
    assert r.status_code == 200
    staff.refresh_from_db()
    assert staff.check_password("An0ther-Secret!")
    assert c.get("/api/auth/me/").status_code == 200  # session kept


def test_password_reset_flow(staff):
    c = APIClient()
    r = c.post("/api/auth/password/reset/", {"email": staff.email}, format="json")
    assert r.status_code == 200
    r2 = c.post("/api/auth/password/reset/", {"email": "nobody@example.com"}, format="json")
    assert r2.json()["message"] == r.json()["message"]  # no account enumeration
    assert len(mail.outbox) == 1
    match = re.search(r"uid=([^&]+)&token=(\S+)", mail.outbox[0].body)
    uid, token = match.groups()
    r = c.post("/api/auth/password/reset/confirm/", {"uid": uid, "token": "bad-token", "new_password": "Br4nd-New-Pass!"}, format="json")
    assert r.json()["error"]["code"] == "INVALID_TOKEN"
    r = c.post("/api/auth/password/reset/confirm/", {"uid": uid, "token": token, "new_password": "Br4nd-New-Pass!"}, format="json")
    assert r.status_code == 200
    staff.refresh_from_db()
    assert staff.check_password("Br4nd-New-Pass!")


def test_profile_update_cannot_escalate_role(staff):
    c = client_for(staff)
    r = c.patch("/api/auth/me/", {"first_name": "Sam", "role": "ADMIN", "is_superuser": True}, format="json")
    assert r.status_code == 200
    staff.refresh_from_db()
    assert staff.first_name == "Sam" and staff.role == Role.STAFF and not staff.is_superuser


# ---------------------------------------------------------------------------
# Role permissions
# ---------------------------------------------------------------------------
@pytest.mark.parametrize(
    "role,method,url,expected",
    [
        (Role.STAFF, "post", "/api/purchases/", 403),
        (Role.STAFF, "post", "/api/customer-receipts/", 403),
        (Role.STAFF, "post", "/api/vendor-payments/", 403),
        (Role.STAFF, "get", "/api/customer-receipts/", 403),
        (Role.STAFF, "get", "/api/audit-logs/", 403),
        (Role.STAFF, "get", "/api/users/", 403),
        (Role.STAFF, "put", "/api/settings/", 403),
        (Role.ACCOUNTANT, "get", "/api/customer-receipts/", 200),
        (Role.ACCOUNTANT, "get", "/api/reports/sales/", 200),
        (Role.ACCOUNTANT, "get", "/api/audit-logs/", 403),
        (Role.ACCOUNTANT, "post", "/api/products/", 403),
        (Role.MANAGER, "get", "/api/audit-logs/", 200),
        (Role.MANAGER, "get", "/api/users/", 403),
        (Role.ADMIN, "get", "/api/users/", 200),
        (Role.ADMIN, "get", "/api/settings/", 200),
    ],
)
def test_role_matrix(role, method, url, expected):
    c = client_for(make_user(role, f"u_{role.lower()}_x"))
    r = getattr(c, method)(url, {}, format="json")
    assert r.status_code == expected, (role, url, r.content)


def test_staff_cannot_cancel_sale(staff, customer, product_factory, make_sale):
    from .conftest import item

    sale = make_sale(customer, [item(product_factory(), 1)])
    r = client_for(staff).post(f"/api/sales/{sale.pk}/cancel/", {"reason": "x"}, format="json")
    assert r.status_code == 403


def test_admin_user_management_is_audited(api, admin_user):
    r = api.post("/api/users/", {"username": "newacc", "email": "n@example.com", "password": "Acc0unt-Pass!", "role": "ACCOUNTANT"}, format="json")
    assert r.status_code == 201, r.json()
    uid = r.json()["data"]["id"]
    assert "password" not in r.json()["data"]
    r = api.patch(f"/api/users/{uid}/", {"role": "MANAGER"}, format="json")
    assert r.json()["data"]["role"] == "MANAGER"
    assert AuditLog.objects.filter(action="PERMISSION_CHANGE", object_id=str(uid)).exists()
    r = api.patch(f"/api/users/{admin_user.pk}/", {"role": "STAFF"}, format="json")
    assert r.json()["error"]["code"] == "SELF_LOCKOUT"
    assert User.objects.get(pk=uid).check_password("Acc0unt-Pass!")


def test_audit_log_api_and_immutability(api, product_factory):
    product_factory()
    d = api.get("/api/audit-logs/?model_name=products.Product").json()["data"]
    assert d["count"] >= 1
    entry = AuditLog.objects.first()
    with pytest.raises(RuntimeError):
        entry.save()
    with pytest.raises(RuntimeError):
        entry.delete()
    assert api.delete(f"/api/audit-logs/{entry.pk}/").status_code == 405


def test_business_settings_update(api):
    r = api.put("/api/settings/", {"business_name": "Nepal Traders", "pan_vat_no": "123456789", "default_tax_rate": "13"}, format="json")
    assert r.status_code == 200 and r.json()["data"]["business_name"] == "Nepal Traders"
    assert api.put("/api/settings/", {"default_tax_rate": "150"}, format="json").status_code == 400


def test_security_headers(api):
    r = api.get("/api/products/")
    assert r["Content-Security-Policy"].startswith("default-src 'none'")
    assert r["X-Content-Type-Options"] == "nosniff"
    assert r["X-Frame-Options"] == "DENY"
    assert r["Cache-Control"] == "no-store"


def test_manage_py_check_deploy_is_clean():
    env = {
        **os.environ,
        "DEBUG": "False",
        "USE_HTTPS": "True",
        "SECRET_KEY": "x" * 20 + "a-very-long-and-random-production-secret-key-1234567890",
        "ALLOWED_HOSTS": "accounting.example.com",
        "DJANGO_SETTINGS_MODULE": "config.settings",
    }
    result = subprocess.run(
        [sys.executable, "manage.py", "check", "--deploy", "--fail-level", "WARNING"],
        cwd=BACKEND, env=env, capture_output=True, text=True, check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "no issues" in result.stdout
