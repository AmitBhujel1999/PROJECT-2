import django_filters
from rest_framework import viewsets

from apps.users.permissions import RolePermission

from .models import AuditLog
from .serializers import AuditLogSerializer


class AuditLogFilter(django_filters.FilterSet):
    start_date = django_filters.DateFilter(field_name="timestamp", lookup_expr="date__gte")
    end_date = django_filters.DateFilter(field_name="timestamp", lookup_expr="date__lte")

    class Meta:
        model = AuditLog
        fields = ["action", "model_name", "object_id", "user"]


class AuditLogViewSet(viewsets.ReadOnlyModelViewSet):
    queryset = AuditLog.objects.select_related("user").all()
    serializer_class = AuditLogSerializer
    permission_classes = [RolePermission]
    permission_map = {"read": "audit.view"}
    filterset_class = AuditLogFilter
    search_fields = ["username", "object_repr", "object_id", "model_name"]
    ordering_fields = ["timestamp", "action", "model_name"]
