from django.urls import path
from rest_framework.routers import DefaultRouter

from . import views

router = DefaultRouter()
router.register("vendor-payments", views.VendorPaymentViewSet, basename="vendor-payment")

urlpatterns = [
    path("vendors/<int:pk>/ledger/", views.VendorLedgerView.as_view(), name="vendor-ledger"),
    path("vendors/<int:pk>/statement/", views.VendorStatementView.as_view(), name="vendor-statement"),
    path("vendors/<int:pk>/aging/", views.VendorAgingView.as_view(), name="vendor-aging"),
    path("vendors/<int:pk>/summary/", views.VendorSummaryView.as_view(), name="vendor-summary"),
    *router.urls,
]
