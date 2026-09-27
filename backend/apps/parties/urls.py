from rest_framework.routers import DefaultRouter

from .views import CustomerViewSet, PartyViewSet, VendorViewSet

router = DefaultRouter()
router.register("parties", PartyViewSet, basename="party")
router.register("customers", CustomerViewSet, basename="customer")
router.register("vendors", VendorViewSet, basename="vendor")
urlpatterns = router.urls
