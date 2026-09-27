from django.db import models

from apps.common.documents import TradeDocument, TradeLine
from apps.common.money import MONEY_FIELD


class Sale(TradeDocument):
    """A customer invoice. Completed invoices are never deleted — they are cancelled."""

    invoice_number = models.CharField(max_length=20, unique=True)
    customer = models.ForeignKey("parties.Party", on_delete=models.PROTECT, related_name="sales")

    class Meta:
        ordering = ["-date", "-id"]
        indexes = [
            models.Index(fields=["customer", "date"]),
            models.Index(fields=["status", "payment_status", "due_date"]),
        ]
        constraints = TradeDocument.document_constraints("sale")

    def __str__(self):
        return self.invoice_number


class SaleItem(TradeLine):
    sale = models.ForeignKey(Sale, on_delete=models.CASCADE, related_name="items")
    product = models.ForeignKey("products.Product", on_delete=models.PROTECT, related_name="sale_items")
    unit_price = models.DecimalField(**MONEY_FIELD)
    total_price = models.DecimalField(**MONEY_FIELD)

    class Meta:
        ordering = ["id"]
        constraints = TradeLine.line_constraints("saleitem", "unit_price", "total_price")

    def __str__(self):
        return f"{self.sale_id}: {self.product_id} x {self.quantity}"
