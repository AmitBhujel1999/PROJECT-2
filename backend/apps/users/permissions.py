"""Role-based permissions, enforced server-side for every API endpoint.

The frontend receives the same list (``/api/auth/me/``) purely to hide
controls; hiding is cosmetic, the checks below are authoritative.
"""

from __future__ import annotations

from rest_framework.permissions import SAFE_METHODS, BasePermission

from .models import Role

ALL = {Role.ADMIN, Role.MANAGER, Role.ACCOUNTANT, Role.STAFF}
MGMT = {Role.ADMIN, Role.MANAGER}
FINANCE = {Role.ADMIN, Role.MANAGER, Role.ACCOUNTANT}

PERMISSION_MATRIX: dict[str, set[str]] = {
    "dashboard.view": ALL,
    "search.use": ALL,
    # Products
    "products.view": ALL,
    "products.manage": MGMT,
    # Parties
    "parties.view": ALL,
    "parties.create": ALL,  # quick-add from sales/purchase entry
    "parties.manage": FINANCE,  # edit credit terms, activate/deactivate
    # Sales
    "sales.view": ALL,
    "sales.create": ALL,
    "sales.cancel": MGMT,
    # Purchases
    "purchases.view": ALL,
    "purchases.create": FINANCE,
    "purchases.cancel": MGMT,
    # Due-date override (manual due date different from credit terms)
    "documents.override_due_date": FINANCE,
    # Receivables / payables
    "receipts.view": FINANCE,
    "receipts.create": FINANCE,
    "receipts.cancel": MGMT,
    "payments.view": FINANCE,
    "payments.create": FINANCE,
    "payments.cancel": MGMT,
    "ledgers.view": FINANCE,
    # Expenses
    "expenses.view": FINANCE,
    "expenses.create": FINANCE,
    "expenses.cancel": MGMT,
    "expenses.manage_categories": MGMT,
    # Inventory
    "inventory.view": ALL,
    "inventory.adjust": MGMT,
    # Reports
    "reports.view": FINANCE,
    # Administration
    "users.manage": {Role.ADMIN},
    "audit.view": MGMT,
    "settings.view": ALL,
    "settings.manage": {Role.ADMIN},
}

ROLE_DESCRIPTIONS = {
    Role.ADMIN: "Full access including users, roles and business settings.",
    Role.MANAGER: "All transactions, cancellations, expense categories, stock adjustments, products, reports and audit log.",
    Role.ACCOUNTANT: "Sales, purchases, receipts, payments, expenses, ledgers, aging and reports.",
    Role.STAFF: "Day-to-day sales entry, product/party lookup and stock view.",
}


def has_perm(user, perm: str) -> bool:
    if user is None or not user.is_authenticated or not user.is_active:
        return False
    if user.is_superuser:
        return True
    return user.role in PERMISSION_MATRIX.get(perm, set())


def permissions_for(user) -> list[str]:
    return sorted(p for p in PERMISSION_MATRIX if has_perm(user, p))


class RolePermission(BasePermission):
    """Checks ``view.permission_map`` keyed by DRF action, HTTP method, or read/write.

    Example::

        permission_map = {"read": "products.view", "write": "products.manage"}
    """

    message = "You do not have permission to perform this action."

    def required(self, request, view) -> str | None:
        mapping = getattr(view, "permission_map", {}) or {}
        action = getattr(view, "action", None)
        if action and action in mapping:
            return mapping[action]
        if request.method in mapping:
            return mapping[request.method]
        return mapping.get("read" if request.method in SAFE_METHODS else "write")

    def has_permission(self, request, view) -> bool:
        if not (request.user and request.user.is_authenticated):
            return False
        perm = self.required(request, view)
        if perm is None:
            return True
        return has_perm(request.user, perm)


def require(perm: str):
    """Permission class for a single fixed permission (function-based APIViews)."""

    class _Required(BasePermission):
        message = "You do not have permission to perform this action."

        def has_permission(self, request, view):
            return has_perm(request.user, perm)

    _Required.__name__ = f"Require_{perm.replace('.', '_')}"
    return _Required
