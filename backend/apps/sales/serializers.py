from rest_framework import serializers

from apps.common.documents import DocumentInputSerializer

from .models import Sale, SaleItem


class SaleInputSerializer(DocumentInputSerializer):
    pass


class SaleItemSerializer(serializers.ModelSerializer):
    product_name = serializers.CharField(source="product.name", read_only=True)
    sku_code = serializers.CharField(source="product.sku_code", read_only=True)
    unit = serializers.CharField(source="product.get_unit_display", read_only=True)

    class Meta:
        model = SaleItem
        fields = [
            "id", "product", "product_name", "sku_code", "unit", "quantity", "unit_price", "gross_amount",
            "discount_type", "discount_value", "discount_amount", "invoice_discount_share", "taxable_amount",
            "tax_rate", "tax_amount", "total_price",
        ]


class SaleListSerializer(serializers.ModelSerializer):
    customer_name = serializers.CharField(source="customer.name", read_only=True)
    balance_due = serializers.DecimalField(max_digits=16, decimal_places=2, read_only=True)
    total_discount = serializers.DecimalField(max_digits=16, decimal_places=2, read_only=True)
    item_count = serializers.IntegerField(read_only=True, default=None)
    total_quantity = serializers.DecimalField(max_digits=14, decimal_places=3, read_only=True, default=None)

    class Meta:
        model = Sale
        fields = [
            "id", "invoice_number", "date", "due_date", "customer", "customer_name", "subtotal",
            "item_discount_total", "discount_type", "discount_value", "discount_amount", "total_discount",
            "taxable_amount", "tax_amount", "total_amount", "amount_paid", "balance_due", "payment_status",
            "status", "item_count", "total_quantity", "created_at",
        ]


class SaleDetailSerializer(SaleListSerializer):
    items = SaleItemSerializer(many=True, read_only=True)
    customer_pan_vat_no = serializers.CharField(source="customer.pan_vat_no", read_only=True)
    customer_address = serializers.CharField(source="customer.address", read_only=True)
    customer_phone = serializers.CharField(source="customer.phone", read_only=True)
    created_by_name = serializers.CharField(source="created_by.username", read_only=True)
    cancelled_by_name = serializers.CharField(source="cancelled_by.username", read_only=True, default=None)
    allocations = serializers.SerializerMethodField()

    class Meta(SaleListSerializer.Meta):
        fields = SaleListSerializer.Meta.fields + [
            "items", "customer_pan_vat_no", "customer_address", "customer_phone", "notes", "created_by_name",
            "cancelled_at", "cancelled_by_name", "cancel_reason", "updated_at", "allocations",
        ]

    def get_allocations(self, obj):
        rel = getattr(obj, "receipt_allocations", None)
        if rel is None:
            return []
        return [
            {
                "id": a.id,
                "receipt_id": a.receipt_id,
                "receipt_number": a.receipt.receipt_number,
                "date": a.date,
                "amount": str(a.amount),
                "is_active": a.is_active,
            }
            for a in rel.select_related("receipt").order_by("date", "id")
        ]


class CancelSerializer(serializers.Serializer):
    reason = serializers.CharField(max_length=255)
