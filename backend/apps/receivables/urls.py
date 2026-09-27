from django.urls import path
from rest_framework.routers import DefaultRouter

from . import views

router = DefaultRouter()
router.register("customer-receipts", views.CustomerReceiptViewSet, basename="customer-receipt")

urlpatterns = [
    path("customers/<int:pk>/ledger/", views.CustomerLedgerView.as_view(), name="customer-ledger"),
    path("customers/<int:pk>/statement/", views.CustomerStatementView.as_view(), name="customer-statement"),
    path("customers/<int:pk>/aging/", views.CustomerAgingView.as_view(), name="customer-aging"),
    path("customers/<int:pk>/summary/", views.CustomerSummaryView.as_view(), name="customer-summary"),
    *router.urls,
]
