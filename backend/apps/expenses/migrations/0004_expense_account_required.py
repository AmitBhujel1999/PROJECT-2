import django.db.models.deletion
from django.db import migrations, models


class Migration(migrations.Migration):
    dependencies = [
        ("expenses", "0003_expense_account"),
        ("cashbank", "0002_backfill_accounts"),
    ]

    operations = [
        migrations.AlterField(
            model_name="expense",
            name="account",
            field=models.ForeignKey(
                help_text="Cash/bank account the expense was paid from.",
                on_delete=django.db.models.deletion.PROTECT,
                related_name="+",
                to="cashbank.moneyaccount",
            ),
        ),
    ]
