from django.db import models

from apps.common.documents import TradeDocument, TradeLine
from apps.common.money import MONEY_FIELD


class Purchase(TradeDocument):
    """A vendor bill. Completed bills are never deleted — they are cancelled."""

    bill_number = models.CharField(max_length=20, unique=True, help_text="Internal number, e.g. BILL-000001.")
    vendor_bill_number = models.CharField(max_length=60, blank=True, default="", db_index=True, help_text="Supplier's own invoice number.")
    vendor = models.ForeignKey("parties.Party", on_delete=models.PROTECT, related_name="purchases")

    class Meta:
        ordering = ["-date", "-id"]
        indexes = [
            models.Index(fields=["vendor", "date"]),
            models.Index(fields=["status", "payment_status", "due_date"]),
        ]
        constraints = TradeDocument.document_constraints("purchase")

    def __str__(self):
        return self.bill_number


class PurchaseItem(TradeLine):
    purchase = models.ForeignKey(Purchase, on_delete=models.CASCADE, related_name="items")
    product = models.ForeignKey("products.Product", on_delete=models.PROTECT, related_name="purchase_items")
    unit_cost = models.DecimalField(**MONEY_FIELD)
    total_cost = models.DecimalField(**MONEY_FIELD)

    class Meta:
        ordering = ["id"]
        constraints = TradeLine.line_constraints("purchaseitem", "unit_cost", "total_cost")

    def __str__(self):
        return f"{self.purchase_id}: {self.product_id} x {self.quantity}"
