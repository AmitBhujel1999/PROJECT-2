from django.urls import path
from rest_framework.routers import DefaultRouter

from .views import ContraViewSet, DayBookView, MoneyAccountViewSet

router = DefaultRouter()
router.register("cash-bank/accounts", MoneyAccountViewSet, basename="money-account")
router.register("cash-bank/contras", ContraViewSet, basename="contra")
urlpatterns = [path("reports/day-book/", DayBookView.as_view(), name="day-book"), *router.urls]
