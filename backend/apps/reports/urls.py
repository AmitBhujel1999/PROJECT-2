from django.urls import path

from . import views

urlpatterns = [
    path("dashboard/", views.DashboardView.as_view(), name="dashboard"),
    path("search/", views.GlobalSearchView.as_view(), name="global-search"),
    path("reports/sales/", views.SalesReportView.as_view(), name="report-sales"),
    path("reports/purchases/", views.PurchaseReportView.as_view(), name="report-purchases"),
    path("reports/stock/", views.StockReportView.as_view(), name="report-stock"),
    path("reports/stock-ledger/", views.StockLedgerReportView.as_view(), name="report-stock-ledger"),
    path("reports/receivables/", views.ReceivablesReportView.as_view(), name="report-receivables"),
    path("reports/payables/", views.PayablesReportView.as_view(), name="report-payables"),
    path("reports/receivables-aging/", views.ReceivablesAgingReportView.as_view(), name="report-receivables-aging"),
    path("reports/payables-aging/", views.PayablesAgingReportView.as_view(), name="report-payables-aging"),
]
