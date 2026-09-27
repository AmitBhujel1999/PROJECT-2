from rest_framework import serializers

from .models import AuditLog


class AuditLogSerializer(serializers.ModelSerializer):
    class Meta:
        model = AuditLog
        fields = [
            "id", "user", "username", "action", "model_name", "object_id", "object_repr",
            "timestamp", "ip_address", "before_data", "after_data",
        ]
