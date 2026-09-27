from rest_framework import serializers

from apps.common.documents import DocumentInputSerializer

from .models import Purchase, PurchaseItem


class PurchaseInputSerializer(DocumentInputSerializer):
    vendor_bill_number = serializers.CharField(max_length=60, required=False, allow_blank=True, default="")


class PurchaseItemSerializer(serializers.ModelSerializer):
    product_name = serializers.CharField(source="product.name", read_only=True)
    sku_code = serializers.CharField(source="product.sku_code", read_only=True)
    unit = serializers.CharField(source="product.get_unit_display", read_only=True)

    class Meta:
        model = PurchaseItem
        fields = [
            "id", "product", "product_name", "sku_code", "unit", "quantity", "unit_cost", "gross_amount",
            "discount_type", "discount_value", "discount_amount", "invoice_discount_share", "taxable_amount",
            "tax_rate", "tax_amount", "total_cost",
        ]


class PurchaseListSerializer(serializers.ModelSerializer):
    vendor_name = serializers.CharField(source="vendor.name", read_only=True)
    balance_due = serializers.DecimalField(max_digits=16, decimal_places=2, read_only=True)
    total_discount = serializers.DecimalField(max_digits=16, decimal_places=2, read_only=True)
    item_count = serializers.IntegerField(read_only=True, default=None)
    total_quantity = serializers.DecimalField(max_digits=14, decimal_places=3, read_only=True, default=None)

    class Meta:
        model = Purchase
        fields = [
            "id", "bill_number", "vendor_bill_number", "date", "due_date", "vendor", "vendor_name", "subtotal",
            "item_discount_total", "discount_type", "discount_value", "discount_amount", "total_discount",
            "taxable_amount", "tax_amount", "total_amount", "amount_paid", "balance_due", "payment_status",
            "status", "item_count", "total_quantity", "created_at",
        ]


class PurchaseDetailSerializer(PurchaseListSerializer):
    items = PurchaseItemSerializer(many=True, read_only=True)
    vendor_pan_vat_no = serializers.CharField(source="vendor.pan_vat_no", read_only=True)
    vendor_address = serializers.CharField(source="vendor.address", read_only=True)
    vendor_phone = serializers.CharField(source="vendor.phone", read_only=True)
    created_by_name = serializers.CharField(source="created_by.username", read_only=True)
    cancelled_by_name = serializers.CharField(source="cancelled_by.username", read_only=True, default=None)
    allocations = serializers.SerializerMethodField()

    class Meta(PurchaseListSerializer.Meta):
        fields = PurchaseListSerializer.Meta.fields + [
            "items", "vendor_pan_vat_no", "vendor_address", "vendor_phone", "notes", "created_by_name",
            "cancelled_at", "cancelled_by_name", "cancel_reason", "updated_at", "allocations",
        ]

    def get_allocations(self, obj):
        rel = getattr(obj, "payment_allocations", None)
        if rel is None:
            return []
        return [
            {
                "id": a.id,
                "payment_id": a.payment_id,
                "payment_number": a.payment.payment_number,
                "date": a.date,
                "amount": str(a.amount),
                "is_active": a.is_active,
            }
            for a in rel.select_related("payment").order_by("date", "id")
        ]


class CancelSerializer(serializers.Serializer):
    reason = serializers.CharField(max_length=255)
