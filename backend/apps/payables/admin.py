from django.contrib import admin

from .models import VendorPayment, VendorPaymentAllocation


class AllocationInline(admin.TabularInline):
    model = VendorPaymentAllocation
    extra = 0
    can_delete = False

    def has_change_permission(self, request, obj=None):
        return False

    def has_add_permission(self, request, obj=None):
        return False


@admin.register(VendorPayment)
class VendorPaymentAdmin(admin.ModelAdmin):
    list_display = ("payment_number", "date", "vendor", "amount", "allocated_amount", "payment_method", "status")
    list_filter = ("status", "payment_method")
    search_fields = ("payment_number", "vendor__name", "reference_number")
    inlines = [AllocationInline]

    def has_change_permission(self, request, obj=None):
        return False

    def has_delete_permission(self, request, obj=None):
        return False
