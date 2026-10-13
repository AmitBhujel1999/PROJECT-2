import django.db.models.deletion
from django.db import migrations, models


class Migration(migrations.Migration):
    dependencies = [
        ("payables", "0002_vendorpayment_account"),
        ("cashbank", "0002_backfill_accounts"),
    ]

    operations = [
        migrations.AlterField(
            model_name="vendorpayment",
            name="account",
            field=models.ForeignKey(
                help_text="Cash/bank account the money came into or went out of.",
                on_delete=django.db.models.deletion.PROTECT,
                related_name="+",
                to="cashbank.moneyaccount",
            ),
        ),
    ]
