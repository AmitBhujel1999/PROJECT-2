from django.db import models

from apps.common.party_payments import PartyPayment, PaymentAllocation


class CustomerReceipt(PartyPayment):
    receipt_number = models.CharField(max_length=20, unique=True)
    customer = models.ForeignKey("parties.Party", on_delete=models.PROTECT, related_name="receipts")

    class Meta:
        ordering = ["-date", "-id"]
        indexes = [models.Index(fields=["customer", "date"])]
        constraints = PartyPayment.payment_constraints("customerreceipt")

    def __str__(self):
        return self.receipt_number


class CustomerReceiptAllocation(PaymentAllocation):
    receipt = models.ForeignKey(CustomerReceipt, on_delete=models.PROTECT, related_name="allocations")
    sale = models.ForeignKey("sales.Sale", on_delete=models.PROTECT, related_name="receipt_allocations")

    class Meta:
        ordering = ["date", "id"]
        indexes = [models.Index(fields=["sale", "is_active"]), models.Index(fields=["receipt", "is_active"])]
        constraints = PaymentAllocation.allocation_constraints("customerreceiptalloc")

    def __str__(self):
        return f"{self.receipt_id} -> {self.sale_id}: {self.amount}"
