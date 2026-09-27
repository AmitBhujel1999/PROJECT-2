from decimal import Decimal

from rest_framework import serializers
from rest_framework.validators import UniqueValidator

from .models import Product


class ProductSerializer(serializers.ModelSerializer):
    sku_code = serializers.CharField(
        max_length=64,
        validators=[UniqueValidator(queryset=Product.objects.all(), message="A product with this SKU already exists.", lookup="iexact")],
    )
    opening_stock = serializers.DecimalField(max_digits=14, decimal_places=3, min_value=Decimal("0"), required=False)
    reorder_level = serializers.DecimalField(max_digits=14, decimal_places=3, min_value=Decimal("0"), required=False)
    purchase_price = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0"), required=False)
    selling_price = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0"), required=False)
    tax_rate = serializers.DecimalField(max_digits=7, decimal_places=2, min_value=Decimal("0"), max_value=Decimal("100"), required=False)
    current_stock = serializers.DecimalField(max_digits=14, decimal_places=3, read_only=True, default=None)
    unit_display = serializers.CharField(source="get_unit_display", read_only=True)

    class Meta:
        model = Product
        fields = [
            "id", "name", "sku_code", "unit", "unit_display", "description", "opening_stock", "reorder_level",
            "purchase_price", "selling_price", "tax_rate", "is_active", "current_stock", "created_at", "updated_at",
        ]
        read_only_fields = ["id", "is_active", "created_at", "updated_at"]

    def validate_sku_code(self, value):
        value = value.strip().upper()
        if not value:
            raise serializers.ValidationError("SKU is required.")
        return value

    def validate_name(self, value):
        value = value.strip()
        if not value:
            raise serializers.ValidationError("Name is required.")
        return value

    def validate_opening_stock(self, value):
        # Opening stock is posted to the immutable stock ledger on creation;
        # later corrections must go through a stock adjustment.
        if self.instance is not None and value != self.instance.opening_stock:
            raise serializers.ValidationError("Opening stock cannot be changed after creation. Use a stock adjustment.")
        return value

    def to_representation(self, instance):
        data = super().to_representation(instance)
        stock = getattr(instance, "current_stock", None)
        data["current_stock"] = None if stock is None else str(stock)
        return data


class ProductLookupSerializer(serializers.ModelSerializer):
    """Compact payload for item pickers."""

    class Meta:
        model = Product
        fields = ["id", "name", "sku_code", "unit", "purchase_price", "selling_price", "tax_rate", "is_active"]
