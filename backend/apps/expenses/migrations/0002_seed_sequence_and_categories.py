from django.db import migrations

CATEGORIES = [
    ("Rent", "Office, shop and warehouse rent"),
    ("Salaries & Wages", "Staff salaries, wages and allowances"),
    ("Utilities", "Electricity, water, internet and phone"),
    ("Transport & Fuel", "Freight, delivery, vehicle fuel and travel"),
    ("Office Supplies", "Stationery, printing and small office items"),
    ("Repairs & Maintenance", "Repairs to premises, equipment and vehicles"),
    ("Marketing & Advertising", "Ads, promotions and signage"),
    ("Bank Charges", "Bank fees, commissions and service charges"),
    ("Professional Fees", "Audit, legal and consulting fees"),
    ("Taxes & Licenses", "Registration renewals, licenses and non-recoverable taxes"),
    ("Miscellaneous", "Expenses that fit no other category"),
]


def seed(apps, schema_editor):
    DocumentSequence = apps.get_model("common", "DocumentSequence")
    ExpenseCategory = apps.get_model("expenses", "ExpenseCategory")
    DocumentSequence.objects.get_or_create(key="expense", defaults={"prefix": "EXP", "padding": 6})
    for name, description in CATEGORIES:
        if not ExpenseCategory.objects.filter(name__iexact=name).exists():
            ExpenseCategory.objects.create(name=name, description=description)


class Migration(migrations.Migration):
    dependencies = [
        ("expenses", "0001_initial"),
        ("common", "0002_seed_sequences_and_settings"),
    ]
    operations = [migrations.RunPython(seed, migrations.RunPython.noop)]
