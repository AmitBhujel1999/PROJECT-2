from django.contrib import admin

from .models import Product


@admin.register(Product)
class ProductAdmin(admin.ModelAdmin):
    list_display = ("sku_code", "name", "unit", "purchase_price", "selling_price", "tax_rate", "is_active")
    list_filter = ("unit", "is_active")
    search_fields = ("name", "sku_code")

    def has_delete_permission(self, request, obj=None):
        return False
