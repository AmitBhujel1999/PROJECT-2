from django.contrib import admin

from .models import Contra, MoneyAccount


@admin.register(MoneyAccount)
class MoneyAccountAdmin(admin.ModelAdmin):
    list_display = ["name", "kind", "account_number", "opening_balance", "is_active"]
    list_filter = ["kind", "is_active"]


@admin.register(Contra)
class ContraAdmin(admin.ModelAdmin):
    list_display = ["contra_number", "date", "from_account", "to_account", "amount", "status"]
    list_filter = ["status"]
