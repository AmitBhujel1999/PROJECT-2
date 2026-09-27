from apps.common import party_account_views as base

from .services import RECEIVABLE


class CustomerReceiptViewSet(base.BasePaymentViewSet):
    cfg = RECEIVABLE
    view_perm = "receipts.view"
    create_perm = "receipts.create"
    cancel_perm = "receipts.cancel"
    success_label = "Receipt"


class CustomerLedgerView(base.PartyLedgerView):
    cfg = RECEIVABLE


class CustomerStatementView(base.PartyStatementView):
    cfg = RECEIVABLE


class CustomerAgingView(base.PartyAgingView):
    cfg = RECEIVABLE


class CustomerSummaryView(base.PartySummaryView):
    cfg = RECEIVABLE
