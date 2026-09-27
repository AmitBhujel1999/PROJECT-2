from decimal import Decimal

from django.core.validators import MaxValueValidator, MinValueValidator
from django.db import models, transaction


class TimeStampedModel(models.Model):
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        abstract = True


class DocumentStatus(models.TextChoices):
    ACTIVE = "ACTIVE", "Active"
    CANCELLED = "CANCELLED", "Cancelled"


class PaymentStatus(models.TextChoices):
    PAID = "PAID", "Paid"
    PARTIAL = "PARTIAL", "Partial"
    UNPAID = "UNPAID", "Unpaid"


class DiscountType(models.TextChoices):
    PERCENTAGE = "PERCENTAGE", "Percentage"
    FIXED = "FIXED", "Fixed amount"


class PaymentMethod(models.TextChoices):
    CASH = "CASH", "Cash"
    BANK = "BANK", "Bank"
    CHEQUE = "CHEQUE", "Cheque"
    ONLINE = "ONLINE", "Online Transfer"
    OTHER = "OTHER", "Other"


class DocumentSequence(models.Model):
    """Gap-free, concurrency-safe document numbering (INV-000001, ...).

    ``next_number`` locks the sequence row with SELECT ... FOR UPDATE, so two
    concurrent transactions can never receive the same number. Document number
    columns additionally carry UNIQUE constraints as a second line of defence.
    """

    key = models.CharField(max_length=32, unique=True)
    prefix = models.CharField(max_length=16)
    padding = models.PositiveSmallIntegerField(default=6)
    last_number = models.PositiveBigIntegerField(default=0)

    SALE = "sale"
    PURCHASE = "purchase"
    RECEIPT = "customer_receipt"
    PAYMENT = "vendor_payment"
    ADJUSTMENT = "stock_adjustment"

    DEFAULTS = {
        SALE: "INV",
        PURCHASE: "BILL",
        RECEIPT: "RCPT",
        PAYMENT: "PAY",
        ADJUSTMENT: "ADJ",
    }

    def __str__(self):
        return f"{self.key} ({self.prefix}) #{self.last_number}"

    @classmethod
    def next_number(cls, key: str) -> str:
        if not transaction.get_connection().in_atomic_block:
            raise RuntimeError("DocumentSequence.next_number must run inside a transaction.")
        seq = cls.objects.select_for_update().filter(key=key).first()
        if seq is None:
            cls.objects.get_or_create(key=key, defaults={"prefix": cls.DEFAULTS.get(key, key.upper()[:6])})
            seq = cls.objects.select_for_update().get(key=key)
        seq.last_number += 1
        seq.save(update_fields=["last_number"])
        return f"{seq.prefix}-{seq.last_number:0{seq.padding}d}"


class BusinessSettings(TimeStampedModel):
    """Singleton holding company identity, currency and tax defaults."""

    business_name = models.CharField(max_length=200, default="My Business Pvt. Ltd.")
    address = models.TextField(blank=True, default="Kathmandu, Nepal")
    pan_vat_no = models.CharField("PAN/VAT No.", max_length=30, blank=True, default="")
    phone = models.CharField(max_length=30, blank=True, default="")
    email = models.EmailField(blank=True, default="")
    currency_code = models.CharField(max_length=3, default="NPR")
    currency_symbol = models.CharField(max_length=8, default="Rs.")
    tax_label = models.CharField(max_length=20, default="VAT")
    default_tax_rate = models.DecimalField(
        max_digits=5,
        decimal_places=2,
        default=Decimal("13.00"),
        validators=[MinValueValidator(Decimal("0")), MaxValueValidator(Decimal("100"))],
    )
    invoice_footer = models.CharField(max_length=300, blank=True, default="Thank you for your business.")

    class Meta:
        verbose_name = "Business settings"
        verbose_name_plural = "Business settings"

    def __str__(self):
        return self.business_name

    def save(self, *args, **kwargs):
        self.pk = 1
        super().save(*args, **kwargs)

    @classmethod
    def get_solo(cls) -> "BusinessSettings":
        obj, _ = cls.objects.get_or_create(pk=1)
        return obj
