from apps.common import party_account_views as base

from .services import PAYABLE


class VendorPaymentViewSet(base.BasePaymentViewSet):
    cfg = PAYABLE
    view_perm = "payments.view"
    create_perm = "payments.create"
    cancel_perm = "payments.cancel"
    success_label = "Payment"


class VendorLedgerView(base.PartyLedgerView):
    cfg = PAYABLE


class VendorStatementView(base.PartyStatementView):
    cfg = PAYABLE


class VendorAgingView(base.PartyAgingView):
    cfg = PAYABLE


class VendorSummaryView(base.PartySummaryView):
    cfg = PAYABLE
