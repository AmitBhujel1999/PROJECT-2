from django.contrib import admin

from .models import StockAdjustment, StockMovement


class ReadOnlyAdmin(admin.ModelAdmin):
    def has_add_permission(self, request):
        return False

    def has_change_permission(self, request, obj=None):
        return False

    def has_delete_permission(self, request, obj=None):
        return False


@admin.register(StockMovement)
class StockMovementAdmin(ReadOnlyAdmin):
    list_display = ("date", "product", "transaction_type", "reference_number", "quantity_in", "quantity_out", "running_balance")
    list_filter = ("transaction_type",)
    search_fields = ("product__name", "product__sku_code", "reference_number")


@admin.register(StockAdjustment)
class StockAdjustmentAdmin(ReadOnlyAdmin):
    list_display = ("adjustment_number", "date", "product", "quantity", "reason", "created_by")
