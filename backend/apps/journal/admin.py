from django.contrib import admin

from .models import JournalLine, JournalVoucher, LedgerAccount


@admin.register(LedgerAccount)
class LedgerAccountAdmin(admin.ModelAdmin):
    list_display = ["name", "group", "opening_balance", "opening_side", "is_active"]
    list_filter = ["group", "is_active"]


class JournalLineInline(admin.TabularInline):
    model = JournalLine
    extra = 0


@admin.register(JournalVoucher)
class JournalVoucherAdmin(admin.ModelAdmin):
    list_display = ["voucher_number", "date", "total_amount", "status"]
    list_filter = ["status"]
    inlines = [JournalLineInline]
