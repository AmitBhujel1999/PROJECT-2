"""Give every existing receipt, vendor payment and expense a cash/bank account.

Cash entries go to "Cash in Hand"; bank, cheque, online and other entries go
to "Bank". The two accounts are created only when something needs them.
"""

from django.db import migrations

CASH_NAME = "Cash in Hand"
BANK_NAME = "Bank"


def backfill(apps, schema_editor):
    MoneyAccount = apps.get_model("cashbank", "MoneyAccount")
    models = [
        apps.get_model("receivables", "CustomerReceipt"),
        apps.get_model("payables", "VendorPayment"),
        apps.get_model("expenses", "Expense"),
    ]
    accounts = {}

    def account(kind, name):
        if kind not in accounts:
            accounts[kind] = MoneyAccount.objects.filter(kind=kind).order_by("id").first() or MoneyAccount.objects.create(
                name=name, kind=kind
            )
        return accounts[kind]

    # Every company starts with a cash account so the first receipt has somewhere to go.
    account("CASH", CASH_NAME)
    for model in models:
        missing = model.objects.filter(account__isnull=True)
        missing.filter(payment_method="CASH").update(account=account("CASH", CASH_NAME))
        if missing.exclude(payment_method="CASH").exists():
            missing.exclude(payment_method="CASH").update(account=account("BANK", BANK_NAME))


class Migration(migrations.Migration):
    dependencies = [
        ("cashbank", "0001_initial"),
        ("receivables", "0002_customerreceipt_account"),
        ("payables", "0002_vendorpayment_account"),
        ("expenses", "0003_expense_account"),
    ]

    operations = [migrations.RunPython(backfill, migrations.RunPython.noop)]
