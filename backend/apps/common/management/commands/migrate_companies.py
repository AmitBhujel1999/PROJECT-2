from django.core.management.base import BaseCommand

from apps.common import companies


class Command(BaseCommand):
    help = "Apply migrations to every extra company schema (run after the normal migrate)."

    def handle(self, *args, **options):
        for slug, _name, schema in companies._registry_rows():
            companies.migrate_schema(schema)
            self.stdout.write(f"Company '{slug}' ({schema}) is up to date.")
