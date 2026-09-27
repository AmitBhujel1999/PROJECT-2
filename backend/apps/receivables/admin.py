from django.contrib import admin

from .models import CustomerReceipt, CustomerReceiptAllocation


class AllocationInline(admin.TabularInline):
    model = CustomerReceiptAllocation
    extra = 0
    can_delete = False

    def has_change_permission(self, request, obj=None):
        return False

    def has_add_permission(self, request, obj=None):
        return False


@admin.register(CustomerReceipt)
class CustomerReceiptAdmin(admin.ModelAdmin):
    list_display = ("receipt_number", "date", "customer", "amount", "allocated_amount", "payment_method", "status")
    list_filter = ("status", "payment_method")
    search_fields = ("receipt_number", "customer__name", "reference_number")
    inlines = [AllocationInline]

    def has_change_permission(self, request, obj=None):
        return False

    def has_delete_permission(self, request, obj=None):
        return False
