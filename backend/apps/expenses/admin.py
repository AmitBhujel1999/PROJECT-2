from django.contrib import admin

from .models import Expense, ExpenseCategory


@admin.register(ExpenseCategory)
class ExpenseCategoryAdmin(admin.ModelAdmin):
    list_display = ("name", "description", "is_active")
    list_filter = ("is_active",)
    search_fields = ("name",)


@admin.register(Expense)
class ExpenseAdmin(admin.ModelAdmin):
    list_display = ("expense_number", "date", "category", "description", "total_amount", "payment_method", "status")
    list_filter = ("status", "category", "payment_method")
    search_fields = ("expense_number", "description", "payee", "vendor__name", "reference_number")

    def has_change_permission(self, request, obj=None):
        return False

    def has_delete_permission(self, request, obj=None):
        return False
