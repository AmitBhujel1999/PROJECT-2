import os

from django.core.management.base import BaseCommand

from apps.users.models import Role, User


class Command(BaseCommand):
    help = "Create the initial administrator from DJANGO_SUPERUSER_* env vars if no users exist."

    def handle(self, *args, **options):
        if User.objects.exists():
            self.stdout.write("Users already exist; skipping admin bootstrap.")
            return
        username = os.environ.get("DJANGO_SUPERUSER_USERNAME")
        password = os.environ.get("DJANGO_SUPERUSER_PASSWORD")
        email = os.environ.get("DJANGO_SUPERUSER_EMAIL", "")
        if not username or not password:
            self.stdout.write(self.style.WARNING("DJANGO_SUPERUSER_USERNAME/PASSWORD not set; no admin created."))
            return
        User.objects.create_superuser(username=username, email=email, password=password, role=Role.ADMIN)
        self.stdout.write(self.style.SUCCESS(f"Administrator '{username}' created."))
