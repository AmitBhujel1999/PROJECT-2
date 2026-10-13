from decimal import Decimal

from rest_framework import serializers

from .models import Contra, MoneyAccount


class MoneyAccountSerializer(serializers.ModelSerializer):
    kind_display = serializers.CharField(source="get_kind_display", read_only=True)
    # Filled by the view only for users who may see balances.
    balance = serializers.DecimalField(max_digits=16, decimal_places=2, read_only=True, default=None)

    class Meta:
        model = MoneyAccount
        fields = [
            "id", "name", "kind", "kind_display", "account_number", "opening_balance", "notes", "is_active", "balance",
            "created_at", "updated_at",
        ]
        read_only_fields = ["id", "is_active", "created_at", "updated_at"]
        # Case-insensitive uniqueness is checked by the service layer.
        validators = []

    def validate_name(self, value):
        value = value.strip()
        if not value:
            raise serializers.ValidationError("Name is required.")
        return value


class ContraInputSerializer(serializers.Serializer):
    date = serializers.DateField()
    from_account = serializers.IntegerField(min_value=1)
    to_account = serializers.IntegerField(min_value=1)
    amount = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0.01"))
    reference_number = serializers.CharField(max_length=60, required=False, allow_blank=True, default="")
    notes = serializers.CharField(max_length=2000, required=False, allow_blank=True, default="")


class ContraSerializer(serializers.ModelSerializer):
    from_account_name = serializers.CharField(source="from_account.name", read_only=True)
    to_account_name = serializers.CharField(source="to_account.name", read_only=True)
    created_by_name = serializers.CharField(source="created_by.username", read_only=True, default=None)
    cancelled_by_name = serializers.CharField(source="cancelled_by.username", read_only=True, default=None)

    class Meta:
        model = Contra
        fields = [
            "id", "contra_number", "date", "from_account", "from_account_name", "to_account", "to_account_name", "amount",
            "reference_number", "notes", "status", "cancelled_at", "cancel_reason", "created_at", "created_by_name",
            "cancelled_by_name",
        ]


class ReasonSerializer(serializers.Serializer):
    reason = serializers.CharField(max_length=255)
