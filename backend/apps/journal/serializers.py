from decimal import Decimal

from rest_framework import serializers

from .models import JournalVoucher, LedgerAccount, normal_side
from .services import line_account


class LedgerAccountSerializer(serializers.ModelSerializer):
    group_display = serializers.CharField(source="get_group_display", read_only=True)
    nature = serializers.CharField(read_only=True)
    # Filled by the view: {"amount": "1000.00", "side": "Dr"}.
    balance = serializers.DictField(read_only=True, default=None)

    class Meta:
        model = LedgerAccount
        fields = [
            "id", "name", "group", "group_display", "nature", "opening_balance", "opening_side", "notes", "is_active",
            "balance", "created_at", "updated_at",
        ]
        read_only_fields = ["id", "is_active", "created_at", "updated_at"]
        # Case-insensitive uniqueness is checked by the service layer.
        validators = []
        extra_kwargs = {"opening_side": {"required": False}}

    def validate_name(self, value):
        value = value.strip()
        if not value:
            raise serializers.ValidationError("Name is required.")
        return value

    def validate_opening_balance(self, value):
        if value < 0:
            raise serializers.ValidationError("Enter the amount and choose Dr or Cr.")
        return value

    def validate(self, attrs):
        # A new ledger's opening side defaults to its group's normal balance.
        if "opening_side" not in attrs and "group" in attrs and self.instance is None:
            attrs["opening_side"] = normal_side(attrs["group"])
        return attrs


class LineInputSerializer(serializers.Serializer):
    account = serializers.CharField(max_length=40)
    debit = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0"), required=False, default=Decimal("0"))
    credit = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0"), required=False, default=Decimal("0"))
    narration = serializers.CharField(max_length=255, required=False, allow_blank=True, default="")


class VoucherInputSerializer(serializers.Serializer):
    date = serializers.DateField()
    narration = serializers.CharField(max_length=2000, required=False, allow_blank=True, default="")
    lines = LineInputSerializer(many=True)


class VoucherSerializer(serializers.ModelSerializer):
    created_by_name = serializers.CharField(source="created_by.username", read_only=True, default=None)
    cancelled_by_name = serializers.CharField(source="cancelled_by.username", read_only=True, default=None)
    lines = serializers.SerializerMethodField()

    class Meta:
        model = JournalVoucher
        fields = [
            "id", "voucher_number", "date", "narration", "total_amount", "status", "cancelled_at", "cancel_reason",
            "created_at", "created_by_name", "cancelled_by_name", "lines",
        ]

    def get_lines(self, voucher):
        return [
            {"line_no": line.line_no, "account": line_account(line), "debit": line.debit, "credit": line.credit, "narration": line.narration}
            for line in voucher.lines.all()
        ]


class VoucherListSerializer(VoucherSerializer):
    """List rows show the accounts in one line: "Furniture / Owner Capital"."""

    accounts = serializers.SerializerMethodField()

    class Meta(VoucherSerializer.Meta):
        fields = ["id", "voucher_number", "date", "narration", "total_amount", "status", "created_at", "accounts"]

    def get_accounts(self, voucher):
        lines = list(voucher.lines.all())
        dr = [line_account(x)["name"] for x in lines if x.debit]
        cr = [line_account(x)["name"] for x in lines if x.credit]
        return {"debit": dr, "credit": cr}


class ReasonSerializer(serializers.Serializer):
    reason = serializers.CharField(max_length=255)

