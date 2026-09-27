from django.conf import settings
from django.core.serializers.json import DjangoJSONEncoder
from django.db import models


class AuditAction(models.TextChoices):
    CREATE = "CREATE", "Create"
    UPDATE = "UPDATE", "Update"
    DELETE = "DELETE", "Delete"
    CANCEL = "CANCEL", "Cancel"
    ACTIVATE = "ACTIVATE", "Activate"
    DEACTIVATE = "DEACTIVATE", "Deactivate"
    ALLOCATE = "ALLOCATE", "Allocate"
    LOGIN = "LOGIN", "Login"
    LOGIN_FAILED = "LOGIN_FAILED", "Login failed"
    LOGOUT = "LOGOUT", "Logout"
    PASSWORD_CHANGE = "PASSWORD_CHANGE", "Password change"
    PASSWORD_RESET = "PASSWORD_RESET", "Password reset"
    PERMISSION_CHANGE = "PERMISSION_CHANGE", "Permission change"


class AuditLog(models.Model):
    """Append-only audit trail. Rows are never updated or deleted by the app."""

    user = models.ForeignKey(settings.AUTH_USER_MODEL, null=True, blank=True, on_delete=models.SET_NULL, related_name="+")
    username = models.CharField(max_length=150, blank=True, default="")
    action = models.CharField(max_length=32, choices=AuditAction.choices, db_index=True)
    model_name = models.CharField(max_length=100, db_index=True)
    object_id = models.CharField(max_length=64, blank=True, default="", db_index=True)
    object_repr = models.CharField(max_length=255, blank=True, default="")
    timestamp = models.DateTimeField(auto_now_add=True, db_index=True)
    ip_address = models.GenericIPAddressField(null=True, blank=True)
    before_data = models.JSONField(null=True, blank=True, encoder=DjangoJSONEncoder)
    after_data = models.JSONField(null=True, blank=True, encoder=DjangoJSONEncoder)

    class Meta:
        ordering = ["-timestamp", "-id"]
        indexes = [models.Index(fields=["model_name", "object_id"])]

    def __str__(self):
        return f"{self.timestamp:%Y-%m-%d %H:%M} {self.username} {self.action} {self.model_name}#{self.object_id}"

    def save(self, *args, **kwargs):
        if self.pk is not None:
            raise RuntimeError("Audit log entries are immutable.")
        super().save(*args, **kwargs)

    def delete(self, *args, **kwargs):
        raise RuntimeError("Audit log entries cannot be deleted.")
