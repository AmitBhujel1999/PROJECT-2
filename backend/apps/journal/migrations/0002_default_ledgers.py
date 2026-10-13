"""Starter ledgers so the chart of accounts is not empty; users add, rename or deactivate them."""

from django.db import migrations

DEFAULT_LEDGERS = [
    ("Owner Capital", "CAPITAL", "CR"),
    ("Drawings", "CAPITAL", "DR"),
    ("Bank Loan", "LOANS", "CR"),
    ("Salary Payable", "CURRENT_LIABILITIES", "CR"),
    ("TDS Payable", "DUTIES_TAXES", "CR"),
    ("Furniture & Fixtures", "FIXED_ASSETS", "DR"),
    ("Office Equipment", "FIXED_ASSETS", "DR"),
    ("Security Deposits", "CURRENT_ASSETS", "DR"),
    ("Interest Income", "INDIRECT_INCOME", "CR"),
    ("Depreciation", "INDIRECT_EXPENSES", "DR"),
]


def seed(apps, schema_editor):
    LedgerAccount = apps.get_model("journal", "LedgerAccount")
    for name, group, side in DEFAULT_LEDGERS:
        if not LedgerAccount.objects.filter(name__iexact=name).exists():
            LedgerAccount.objects.create(name=name, group=group, opening_side=side)


class Migration(migrations.Migration):
    dependencies = [("journal", "0001_initial")]

    operations = [migrations.RunPython(seed, migrations.RunPython.noop)]
