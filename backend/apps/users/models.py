from django.contrib.auth.models import AbstractUser
from django.db import models


class Role(models.TextChoices):
    ADMIN = "ADMIN", "Admin"
    MANAGER = "MANAGER", "Manager"
    ACCOUNTANT = "ACCOUNTANT", "Accountant"
    STAFF = "STAFF", "Staff"


class User(AbstractUser):
    role = models.CharField(max_length=20, choices=Role.choices, default=Role.STAFF, db_index=True)
    phone = models.CharField(max_length=30, blank=True, default="")

    class Meta:
        ordering = ["username"]

    def save(self, *args, **kwargs):
        # Django superusers are always application administrators.
        if self.is_superuser:
            self.role = Role.ADMIN
        super().save(*args, **kwargs)

    @property
    def display_name(self) -> str:
        return self.get_full_name() or self.username
