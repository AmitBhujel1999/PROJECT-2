"""PostgreSQL trigger that forbids UPDATE/DELETE on the stock ledger.

Application code already refuses to modify ledger rows; this makes the rule
hold even for raw SQL or admin mistakes. Corrections are made by appending
reversing / adjustment movements.
"""

from django.db import migrations

FORWARD = """
CREATE OR REPLACE FUNCTION inventory_stockmovement_immutable() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'Stock ledger entries are immutable (% not allowed)', TG_OP;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS inventory_stockmovement_no_modify ON inventory_stockmovement;
CREATE TRIGGER inventory_stockmovement_no_modify
    BEFORE UPDATE OR DELETE ON inventory_stockmovement
    FOR EACH ROW EXECUTE FUNCTION inventory_stockmovement_immutable();
"""

REVERSE = """
DROP TRIGGER IF EXISTS inventory_stockmovement_no_modify ON inventory_stockmovement;
DROP FUNCTION IF EXISTS inventory_stockmovement_immutable();
"""


class Migration(migrations.Migration):
    dependencies = [("inventory", "0001_initial")]
    operations = [migrations.RunSQL(FORWARD, REVERSE)]
