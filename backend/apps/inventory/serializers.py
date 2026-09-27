from decimal import Decimal

from rest_framework import serializers

from apps.products.models import Product

from .models import AdjustmentReason, StockAdjustment


class StockRegisterSerializer(serializers.ModelSerializer):
    unit_display = serializers.CharField(source="get_unit_display", read_only=True)
    total_purchased = serializers.DecimalField(max_digits=14, decimal_places=3, read_only=True)
    total_sold = serializers.DecimalField(max_digits=14, decimal_places=3, read_only=True)
    total_adjusted = serializers.DecimalField(max_digits=14, decimal_places=3, read_only=True)
    current_stock = serializers.DecimalField(max_digits=14, decimal_places=3, read_only=True)
    cost_value = serializers.DecimalField(max_digits=20, decimal_places=2, read_only=True)
    retail_value = serializers.DecimalField(max_digits=20, decimal_places=2, read_only=True)
    stock_status = serializers.CharField(read_only=True)

    class Meta:
        model = Product
        fields = [
            "id", "sku_code", "name", "unit", "unit_display", "opening_stock", "total_purchased", "total_sold",
            "total_adjusted", "current_stock", "purchase_price", "selling_price", "cost_value", "retail_value",
            "reorder_level", "stock_status", "is_active",
        ]


class StockAdjustmentSerializer(serializers.ModelSerializer):
    product_name = serializers.CharField(source="product.name", read_only=True)
    sku_code = serializers.CharField(source="product.sku_code", read_only=True)
    reason_display = serializers.CharField(source="get_reason_display", read_only=True)
    created_by_name = serializers.CharField(source="created_by.username", read_only=True)

    class Meta:
        model = StockAdjustment
        fields = [
            "id", "adjustment_number", "product", "product_name", "sku_code", "date", "quantity", "reason",
            "reason_display", "notes", "stock_before", "stock_after", "created_at", "created_by_name",
        ]


class StockAdjustmentCreateSerializer(serializers.Serializer):
    product = serializers.PrimaryKeyRelatedField(queryset=Product.objects.filter(is_active=True))
    date = serializers.DateField()
    quantity = serializers.DecimalField(max_digits=14, decimal_places=3)
    reason = serializers.ChoiceField(choices=AdjustmentReason.choices)
    notes = serializers.CharField(max_length=255, allow_blank=True, required=False, default="")

    def validate_quantity(self, value):
        if value == Decimal("0"):
            raise serializers.ValidationError("Quantity cannot be zero.")
        return value

    def validate(self, attrs):
        if attrs["reason"] == AdjustmentReason.OTHER and not attrs.get("notes", "").strip():
            raise serializers.ValidationError({"notes": "Please describe the reason for this adjustment."})
        return attrs
