from django.contrib import admin

from .models import Party


@admin.register(Party)
class PartyAdmin(admin.ModelAdmin):
    list_display = ("name", "type", "phone", "pan_vat_no", "credit_terms", "credit_limit", "is_active")
    list_filter = ("type", "is_active", "credit_terms")
    search_fields = ("name", "phone", "pan_vat_no", "email")

    def has_delete_permission(self, request, obj=None):
        return False
