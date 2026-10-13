from django.urls import path

from . import statement_views, views

urlpatterns = [
    path("dashboard/", views.DashboardView.as_view(), name="dashboard"),
    path("search/", views.GlobalSearchView.as_view(), name="global-search"),
    path("reports/sales/", views.SalesReportView.as_view(), name="report-sales"),
    path("reports/purchases/", views.PurchaseReportView.as_view(), name="report-purchases"),
    path("reports/expenses/", views.ExpenseReportView.as_view(), name="report-expenses"),
    path("reports/stock/", views.StockReportView.as_view(), name="report-stock"),
    path("reports/stock-ledger/", views.StockLedgerReportView.as_view(), name="report-stock-ledger"),
    path("reports/receivables/", views.ReceivablesReportView.as_view(), name="report-receivables"),
    path("reports/payables/", views.PayablesReportView.as_view(), name="report-payables"),
    path("reports/profit-loss/", statement_views.ProfitLossView.as_view(), name="report-profit-loss"),
    path("reports/balance-sheet/", statement_views.BalanceSheetView.as_view(), name="report-balance-sheet"),
    path("reports/cash-flow/", statement_views.CashFlowView.as_view(), name="report-cash-flow"),
    path("reports/receivables-aging/", views.ReceivablesAgingReportView.as_view(), name="report-receivables-aging"),
    path("reports/payables-aging/", views.PayablesAgingReportView.as_view(), name="report-payables-aging"),
]
