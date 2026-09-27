"""Abstract models for money received from customers / paid to vendors.

CustomerReceipt (receivables) and VendorPayment (payables) are concrete
subclasses with their own tables. A payment's ``allocated_amount`` is a cache
of its active allocations maintained only by the service layer, guarded by a
CHECK constraint (allocated <= amount) so over-allocation is impossible even
under concurrency. Whatever is not allocated is an advance / unallocated
payment, which never appears as an invoice balance in aging.
"""

from django.conf import settings
from django.db import models
from django.db.models import F, Q

from .models import DocumentStatus, PaymentMethod, TimeStampedModel
from .money import MONEY_FIELD, ZERO, money


class PartyPayment(TimeStampedModel):
    date = models.DateField(db_index=True)
    amount = models.DecimalField(**MONEY_FIELD)
    allocated_amount = models.DecimalField(**MONEY_FIELD, default=ZERO)
    payment_method = models.CharField(max_length=10, choices=PaymentMethod.choices, default=PaymentMethod.CASH)
    reference_number = models.CharField(max_length=60, blank=True, default="", help_text="Cheque no., bank ref, etc.")
    notes = models.TextField(blank=True, default="")
    status = models.CharField(max_length=10, choices=DocumentStatus.choices, default=DocumentStatus.ACTIVE, db_index=True)
    cancelled_at = models.DateTimeField(null=True, blank=True)
    cancel_reason = models.CharField(max_length=255, blank=True, default="")
    created_by = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.PROTECT, related_name="+")
    cancelled_by = models.ForeignKey(settings.AUTH_USER_MODEL, null=True, blank=True, on_delete=models.PROTECT, related_name="+")

    class Meta:
        abstract = True

    @property
    def unallocated_amount(self):
        if self.status == DocumentStatus.CANCELLED:
            return money(ZERO)
        return money(self.amount - self.allocated_amount)

    @staticmethod
    def payment_constraints(prefix: str) -> list:
        return [
            models.CheckConstraint(condition=Q(amount__gt=0), name=f"{prefix}_amount_gt_0"),
            models.CheckConstraint(
                condition=Q(allocated_amount__gte=0) & Q(allocated_amount__lte=F("amount")),
                name=f"{prefix}_allocated_within_amount",
            ),
        ]


class PaymentAllocation(models.Model):
    """Links part of a payment to an invoice/bill.

    Allocations are never deleted. Un-allocating (or cancelling either side)
    marks the row inactive with ``voided_at`` so historical aging can still
    reconstruct what was outstanding on any past date.

    ``date`` is the effective settlement date: the later of the payment date
    and the document date.
    """

    amount = models.DecimalField(**MONEY_FIELD)
    date = models.DateField(db_index=True)
    is_active = models.BooleanField(default=True, db_index=True)
    created_at = models.DateTimeField(auto_now_add=True)
    created_by = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.PROTECT, related_name="+")
    voided_at = models.DateTimeField(null=True, blank=True)
    voided_by = models.ForeignKey(settings.AUTH_USER_MODEL, null=True, blank=True, on_delete=models.PROTECT, related_name="+")
    void_reason = models.CharField(max_length=255, blank=True, default="")

    class Meta:
        abstract = True

    @staticmethod
    def allocation_constraints(prefix: str) -> list:
        return [models.CheckConstraint(condition=Q(amount__gt=0), name=f"{prefix}_amount_gt_0")]
