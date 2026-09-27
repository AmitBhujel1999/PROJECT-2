from django.urls import path
from rest_framework.routers import DefaultRouter

from . import views

router = DefaultRouter()
router.register("inventory/adjustments", views.StockAdjustmentViewSet, basename="stock-adjustment")

urlpatterns = [
    path("inventory/", views.StockRegisterView.as_view(), name="stock-register"),
    path("inventory/ledger/", views.StockLedgerView.as_view(), name="stock-ledger"),
    path("inventory/<int:product_id>/", views.ProductStockView.as_view(), name="product-stock"),
    *router.urls,
]
