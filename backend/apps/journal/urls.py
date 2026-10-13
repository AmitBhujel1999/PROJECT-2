from rest_framework.routers import DefaultRouter

from .views import JournalVoucherViewSet, LedgerAccountViewSet

router = DefaultRouter()
router.register("journal/ledgers", LedgerAccountViewSet, basename="ledger-account")
router.register("journal/vouchers", JournalVoucherViewSet, basename="journal-voucher")
urlpatterns = router.urls
