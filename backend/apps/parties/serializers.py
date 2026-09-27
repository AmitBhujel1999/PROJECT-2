from decimal import Decimal

from rest_framework import serializers

from .models import CreditTerms, Party


class PartySerializer(serializers.ModelSerializer):
    credit_limit = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0"), required=False)
    credit_terms_display = serializers.CharField(source="get_credit_terms_display", read_only=True)
    type_display = serializers.CharField(source="get_type_display", read_only=True)

    class Meta:
        model = Party
        fields = [
            "id", "type", "type_display", "name", "pan_vat_no", "phone", "email", "address",
            "credit_terms", "credit_terms_display", "credit_days", "credit_limit", "notes",
            "is_active", "created_at", "updated_at",
        ]
        read_only_fields = ["id", "is_active", "created_at", "updated_at"]

    def validate_name(self, value):
        value = value.strip()
        if not value:
            raise serializers.ValidationError("Name is required.")
        return value

    def validate(self, attrs):
        terms = attrs.get("credit_terms", getattr(self.instance, "credit_terms", CreditTerms.CASH))
        if terms == CreditTerms.CUSTOM and attrs.get("credit_days") is None and self.instance is None:
            raise serializers.ValidationError({"credit_days": "Credit days are required for custom credit terms."})
        return attrs
