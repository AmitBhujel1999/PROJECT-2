import django_filters
from django.db.models import Prefetch
from rest_framework import mixins, viewsets
from rest_framework.decorators import action

from apps.common.responses import created, ok
from apps.common.utils import parse_date_param
from apps.reports.documents_pdf import journal_pdf
from apps.reports.exporters import Column, export_response, pdf_response
from apps.users.permissions import RolePermission

from . import services
from .models import AccountGroup, JournalLine, JournalVoucher, LedgerAccount
from .serializers import (
    LedgerAccountSerializer,
    ReasonSerializer,
    VoucherInputSerializer,
    VoucherListSerializer,
    VoucherSerializer,
)


class LedgerAccountViewSet(viewsets.ModelViewSet):
    """Chart of accounts. Balances are signed Dr/Cr."""

    serializer_class = LedgerAccountSerializer
    permission_classes = [RolePermission]
    permission_map = {"read": "journal.view", "write": "journal.manage_ledgers"}
    filterset_fields = ["is_active", "group"]
    search_fields = ["name"]
    ordering_fields = ["name", "group"]
    ordering = ["name"]
    queryset = LedgerAccount.objects.all()

    def _with_balance(self, ledgers):
        balances = services.ledger_balances()
        for lg in ledgers:
            lg.balance = services.dr_cr(balances.get(lg.pk, 0))
        return ledgers

    def _data(self, ledger):
        return self.get_serializer(self._with_balance([ledger])[0]).data

    def list(self, request, *args, **kwargs):
        page = self.paginate_queryset(self.filter_queryset(self.get_queryset()))
        return self.get_paginated_response(self.get_serializer(self._with_balance(list(page)), many=True).data)

    def retrieve(self, request, *args, **kwargs):
        return ok(self._data(self.get_object()))

    def create(self, request, *args, **kwargs):
        serializer = self.get_serializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        ledger = services.create_ledger(data=serializer.validated_data, user=request.user)
        return created(self._data(ledger), f"Ledger {ledger.name} created.")

    def update(self, request, *args, **kwargs):
        partial = kwargs.pop("partial", False)
        instance = self.get_object()
        serializer = self.get_serializer(instance, data=request.data, partial=partial)
        serializer.is_valid(raise_exception=True)
        ledger = services.update_ledger(instance, data=dict(serializer.validated_data), user=request.user)
        return ok(self._data(ledger), f"Ledger {ledger.name} updated.")

    def destroy(self, request, *args, **kwargs):
        services.delete_ledger(self.get_object(), user=request.user)
        return ok(None, "Ledger deleted.")

    @action(detail=True, methods=["post"])
    def deactivate(self, request, pk=None):
        ledger = services.set_ledger_active(self.get_object(), active=False, user=request.user)
        return ok(self._data(ledger), f"{ledger.name} deactivated.")

    @action(detail=True, methods=["post"])
    def activate(self, request, pk=None):
        ledger = services.set_ledger_active(self.get_object(), active=True, user=request.user)
        return ok(self._data(ledger), f"{ledger.name} activated.")

    @action(detail=False, methods=["get"])
    def groups(self, request):
        from .models import GROUP_NATURE

        return ok([{"value": g.value, "label": g.label, "nature": GROUP_NATURE[g]} for g in AccountGroup])

    @action(detail=True, methods=["get"])
    def book(self, request, pk=None):
        ledger = self.get_object()
        data = services.ledger_book(ledger, start=parse_date_param(request, "start_date"), end=parse_date_param(request, "end_date"))
        if request.query_params.get("export"):
            fmt = lambda b: f"{b['amount']:,.2f} {b['side']}"  # noqa: E731
            columns = [
                Column("date", "Date", width=0.8), Column("number", "Voucher", width=0.9), Column("narration", "Particulars", width=2.6),
                Column("debit", "Debit", numeric=True), Column("credit", "Credit", numeric=True), Column("balance_text", "Balance", width=1.1),
            ]
            rows = [{"date": data["start_date"] or "", "narration": "Opening Balance", "balance_text": fmt(data["opening"])}]
            rows += [{**e, "balance_text": fmt(e["balance"])} for e in data["entries"]]
            totals = {"narration": "Closing Balance", "debit": data["total_debit"], "credit": data["total_credit"],
                      "balance_text": fmt(data["closing"])}
            response = export_response(
                request, base_name=f"ledger-{ledger.pk}", title=f"Ledger: {ledger.name}", columns=columns, rows=rows,
                meta=[("Group", ledger.get_group_display()), ("From", data["start_date"] or "Beginning"), ("To", data["end_date"])],
                totals=totals,
            )
            if response:
                return response
        return ok(data)


class VoucherFilter(django_filters.FilterSet):
    start_date = django_filters.DateFilter(field_name="date", lookup_expr="gte")
    end_date = django_filters.DateFilter(field_name="date", lookup_expr="lte")
    account = django_filters.CharFilter(method="filter_account")

    class Meta:
        model = JournalVoucher
        fields = ["status"]

    def filter_account(self, qs, name, value):
        kind, _, raw = value.partition(":")
        field = {"ledger": "ledger", "cash": "money_account", "party": "party", "expense": "expense_category"}.get(kind)
        if not field or not raw.isdigit():
            return qs.none()
        return qs.filter(**{f"lines__{field}_id": int(raw)}).distinct()


class JournalVoucherViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, mixins.CreateModelMixin, viewsets.GenericViewSet):
    """Journal vouchers are never edited or deleted — they are cancelled."""

    permission_classes = [RolePermission]
    permission_map = {
        "read": "journal.view",
        "accounts": "journal.create",
        "pdf": "journal.view",
        "create": "journal.create",
        "cancel": "journal.cancel",
    }
    filterset_class = VoucherFilter
    search_fields = ["voucher_number", "narration", "lines__narration"]
    ordering_fields = ["date", "voucher_number", "total_amount"]
    ordering = ["-date", "-id"]

    def get_queryset(self):
        lines = JournalLine.objects.select_related("ledger", "money_account", "party", "expense_category").order_by("line_no")
        return JournalVoucher.objects.select_related("created_by", "cancelled_by").prefetch_related(Prefetch("lines", queryset=lines))

    def get_serializer_class(self):
        return VoucherListSerializer if self.action == "list" else VoucherSerializer

    def filter_queryset(self, queryset):
        qs = super().filter_queryset(queryset)
        return qs.distinct() if self.request.query_params.get("search") else qs

    def create(self, request, *args, **kwargs):
        serializer = VoucherInputSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        voucher = services.create_voucher(data=serializer.validated_data, user=request.user)
        voucher = self.get_queryset().get(pk=voucher.pk)
        return created(VoucherSerializer(voucher).data, f"Journal voucher {voucher.voucher_number} recorded successfully.")

    @action(detail=True, methods=["post"])
    def cancel(self, request, pk=None):
        serializer = ReasonSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        voucher = services.cancel_voucher(self.get_object(), reason=serializer.validated_data["reason"], user=request.user)
        return ok(VoucherSerializer(self.get_queryset().get(pk=voucher.pk)).data, f"Journal voucher {voucher.voucher_number} cancelled.")

    @action(detail=True, methods=["get"])
    def pdf(self, request, pk=None):
        voucher = self.get_object()
        return pdf_response(journal_pdf(voucher), f"{voucher.voucher_number}.pdf", inline=request.query_params.get("inline") == "1")

    @action(detail=False, methods=["get"])
    def accounts(self, request):
        """Every account a line can post to, for the account search box."""
        return ok(services.account_options(request.query_params.get("search", "")[:100]))

