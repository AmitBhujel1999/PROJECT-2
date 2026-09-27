from django.http import JsonResponse


def csrf_failure(request, reason=""):
    return JsonResponse(
        {"success": False, "error": {"code": "CSRF_FAILED", "message": "CSRF verification failed. Refresh the page and try again."}},
        status=403,
    )


from django.db import transaction  # noqa: E402
from rest_framework.views import APIView  # noqa: E402

from apps.audit.models import AuditAction  # noqa: E402
from apps.audit.services import record, snapshot  # noqa: E402
from apps.users.permissions import RolePermission  # noqa: E402

from .models import BusinessSettings  # noqa: E402
from .responses import ok  # noqa: E402
from .serializers import BusinessSettingsSerializer  # noqa: E402


class BusinessSettingsView(APIView):
    permission_classes = [RolePermission]
    permission_map = {"read": "settings.view", "write": "settings.manage"}

    def get(self, request):
        return ok(BusinessSettingsSerializer(BusinessSettings.get_solo()).data)

    @transaction.atomic
    def put(self, request):
        obj = BusinessSettings.get_solo()
        before = snapshot(obj)
        serializer = BusinessSettingsSerializer(obj, data=request.data, partial=True)
        serializer.is_valid(raise_exception=True)
        serializer.save()
        record(AuditAction.UPDATE, obj, before=before, after=snapshot(obj))
        return ok(serializer.data, "Settings saved.")

    patch = put
