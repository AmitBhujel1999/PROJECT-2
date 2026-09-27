from django.contrib import admin

from .models import BusinessSettings, DocumentSequence


@admin.register(BusinessSettings)
class BusinessSettingsAdmin(admin.ModelAdmin):
    list_display = ("business_name", "pan_vat_no", "currency_code", "default_tax_rate")


@admin.register(DocumentSequence)
class DocumentSequenceAdmin(admin.ModelAdmin):
    list_display = ("key", "prefix", "last_number")
    readonly_fields = ("last_number",)
