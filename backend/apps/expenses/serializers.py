from decimal import Decimal

from rest_framework import serializers

from apps.common.models import PaymentMethod
from apps.common.money import ZERO

from .models import Expense, ExpenseCategory


class ExpenseCategorySerializer(serializers.ModelSerializer):
    expense_count = serializers.IntegerField(read_only=True, default=None)

    class Meta:
        model = ExpenseCategory
        fields = ["id", "name", "description", "is_active", "expense_count", "created_at", "updated_at"]
        read_only_fields = ["id", "is_active", "created_at", "updated_at"]
        # Case-insensitive uniqueness is checked by the service layer.
        validators = []

    def validate_name(self, value):
        value = value.strip()
        if not value:
            raise serializers.ValidationError("Name is required.")
        return value


class ExpenseInputSerializer(serializers.Serializer):
    """Totals are never accepted from the client."""

    date = serializers.DateField()
    category = serializers.IntegerField(min_value=1)
    vendor = serializers.IntegerField(min_value=1, required=False, allow_null=True)
    payee = serializers.CharField(max_length=200, required=False, allow_blank=True, default="")
    description = serializers.CharField(max_length=255)
    amount = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0.01"))
    tax_rate = serializers.DecimalField(
        max_digits=7, decimal_places=2, min_value=ZERO, max_value=Decimal("100"), required=False, default=ZERO
    )
    payment_method = serializers.ChoiceField(choices=PaymentMethod.choices, required=False, default=PaymentMethod.CASH)
    reference_number = serializers.CharField(max_length=60, required=False, allow_blank=True, default="")
    notes = serializers.CharField(required=False, allow_blank=True, default="", max_length=2000)

    def validate_description(self, value):
        value = value.strip()
        if not value:
            raise serializers.ValidationError("Description is required.")
        return value


class ExpenseCalculateSerializer(serializers.Serializer):
    amount = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0.01"))
    tax_rate = serializers.DecimalField(
        max_digits=7, decimal_places=2, min_value=ZERO, max_value=Decimal("100"), required=False, default=ZERO
    )


class ExpenseListSerializer(serializers.ModelSerializer):
    category_name = serializers.CharField(source="category.name", read_only=True)
    vendor_name = serializers.CharField(source="vendor.name", read_only=True, default=None)
    paid_to = serializers.CharField(read_only=True)
    payment_method_display = serializers.CharField(source="get_payment_method_display", read_only=True)

    class Meta:
        model = Expense
        fields = [
            "id", "expense_number", "date", "category", "category_name", "vendor", "vendor_name", "payee", "paid_to",
            "description", "amount", "tax_rate", "tax_amount", "total_amount", "payment_method",
            "payment_method_display", "reference_number", "status", "created_at",
        ]


class ExpenseDetailSerializer(ExpenseListSerializer):
    created_by_name = serializers.CharField(source="created_by.username", read_only=True)
    cancelled_by_name = serializers.CharField(source="cancelled_by.username", read_only=True, default=None)

    class Meta(ExpenseListSerializer.Meta):
        fields = ExpenseListSerializer.Meta.fields + [
            "notes", "created_by_name", "cancelled_at", "cancelled_by_name", "cancel_reason", "updated_at",
        ]


class CancelSerializer(serializers.Serializer):
    reason = serializers.CharField(max_length=255)
