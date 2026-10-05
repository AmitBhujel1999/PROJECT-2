from django.db import migrations

# The registry of extra companies always lives in the public schema, even when
# this migration runs inside a company schema (see apps.common.companies).
FORWARD = """
CREATE TABLE IF NOT EXISTS public.acct_company (
    slug varchar(50) PRIMARY KEY,
    name varchar(200) NOT NULL,
    schema_name varchar(63) NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now()
);
"""


class Migration(migrations.Migration):
    dependencies = [("common", "0002_seed_sequences_and_settings")]
    operations = [migrations.RunSQL(FORWARD, migrations.RunSQL.noop)]
