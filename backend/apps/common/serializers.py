from rest_framework import serializers

from .models import BusinessSettings


class BusinessSettingsSerializer(serializers.ModelSerializer):
    class Meta:
        model = BusinessSettings
        fields = [
            "business_name", "address", "pan_vat_no", "phone", "email", "currency_code",
            "currency_symbol", "tax_label", "default_tax_rate", "invoice_footer", "updated_at",
        ]
        read_only_fields = ["updated_at"]
