from django.db import models

from apps.common.party_payments import PartyPayment, PaymentAllocation


class VendorPayment(PartyPayment):
    payment_number = models.CharField(max_length=20, unique=True)
    vendor = models.ForeignKey("parties.Party", on_delete=models.PROTECT, related_name="vendor_payments")

    class Meta:
        ordering = ["-date", "-id"]
        indexes = [models.Index(fields=["vendor", "date"])]
        constraints = PartyPayment.payment_constraints("vendorpayment")

    def __str__(self):
        return self.payment_number


class VendorPaymentAllocation(PaymentAllocation):
    payment = models.ForeignKey(VendorPayment, on_delete=models.PROTECT, related_name="allocations")
    purchase = models.ForeignKey("purchases.Purchase", on_delete=models.PROTECT, related_name="payment_allocations")

    class Meta:
        ordering = ["date", "id"]
        indexes = [models.Index(fields=["purchase", "is_active"]), models.Index(fields=["payment", "is_active"])]
        constraints = PaymentAllocation.allocation_constraints("vendorpaymentalloc")

    def __str__(self):
        return f"{self.payment_id} -> {self.purchase_id}: {self.amount}"
