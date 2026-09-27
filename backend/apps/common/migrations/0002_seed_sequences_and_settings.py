from django.db import migrations

SEQUENCES = {
    "sale": "INV",
    "purchase": "BILL",
    "customer_receipt": "RCPT",
    "vendor_payment": "PAY",
    "stock_adjustment": "ADJ",
}


def seed(apps, schema_editor):
    DocumentSequence = apps.get_model("common", "DocumentSequence")
    BusinessSettings = apps.get_model("common", "BusinessSettings")
    for key, prefix in SEQUENCES.items():
        DocumentSequence.objects.get_or_create(key=key, defaults={"prefix": prefix, "padding": 6})
    BusinessSettings.objects.get_or_create(pk=1)


class Migration(migrations.Migration):
    dependencies = [("common", "0001_initial")]
    operations = [migrations.RunPython(seed, migrations.RunPython.noop)]
