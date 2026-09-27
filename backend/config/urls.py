from django.contrib import admin
from django.http import JsonResponse
from django.urls import path


def health(request):
    return JsonResponse({"success": True, "data": {"status": "ok"}, "message": ""})


urlpatterns = [
    path("admin/", admin.site.urls),
    path("api/health/", health, name="health"),
]
