from django.contrib import admin
from django.http import JsonResponse
from django.urls import include, path

from apps.common.views import BusinessSettingsView
from apps.users.urls import auth_urlpatterns


def health(request):
    return JsonResponse({"success": True, "data": {"status": "ok"}, "message": ""})


api_patterns = [
    path("health/", health, name="health"),
    path("auth/", include(auth_urlpatterns)),
    path("settings/", BusinessSettingsView.as_view(), name="business-settings"),
    path("", include("apps.users.urls")),
    path("", include("apps.audit.urls")),
    path("", include("apps.products.urls")),
    path("", include("apps.parties.urls")),
    path("", include("apps.inventory.urls")),
]

urlpatterns = [
    path("admin/", admin.site.urls),
    path("api/", include(api_patterns)),
]
