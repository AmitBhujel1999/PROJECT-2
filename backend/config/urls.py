from django.contrib import admin
from django.http import JsonResponse
from django.urls import include, path

from apps.users.urls import auth_urlpatterns


def health(request):
    return JsonResponse({"success": True, "data": {"status": "ok"}, "message": ""})


api_patterns = [
    path("health/", health, name="health"),
    path("auth/", include(auth_urlpatterns)),
    path("", include("apps.users.urls")),
]

urlpatterns = [
    path("admin/", admin.site.urls),
    path("api/", include(api_patterns)),
]
