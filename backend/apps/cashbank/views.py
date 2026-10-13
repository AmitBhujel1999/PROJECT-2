import django_filters
from django.db.models import Q
from django.utils import timezone
from rest_framework import mixins, viewsets
from rest_framework.decorators import action
from rest_framework.views import APIView

from apps.common.responses import created, ok
from apps.common.utils import parse_date_param
from apps.reports.exporters import Column, export_response
from apps.users.permissions import RolePermission, has_perm

from . import services
from .models import Contra, MoneyAccount
from .serializers import ContraInputSerializer, ContraSerializer, MoneyAccountSerializer, ReasonSerializer


class MoneyAccountViewSet(viewsets.ModelViewSet):
    """Cash/bank accounts. Anyone taking payment may list them; balances need cashbank.view."""

    serializer_class = MoneyAccountSerializer
    permission_classes = [RolePermission]
    permission_map = {"read": "cashbank.list", "book": "cashbank.view", "write": "cashbank.manage"}
    filterset_fields = ["is_active", "kind"]
    search_fields = ["name", "account_number"]
    ordering_fields = ["name", "kind", "created_at"]
    ordering = ["name"]
    queryset = MoneyAccount.objects.all()

    def _with_balance(self, accounts):
        if has_perm(self.request.user, "cashbank.view"):
            balances = services.balances()
            for a in accounts:
                a.balance = balances.get(a.pk)
        return accounts

    def _data(self, account):
        return self.get_serializer(self._with_balance([account])[0]).data

    def list(self, request, *args, **kwargs):
        page = self.paginate_queryset(self.filter_queryset(self.get_queryset()))
        return self.get_paginated_response(self.get_serializer(self._with_balance(list(page)), many=True).data)

    def retrieve(self, request, *args, **kwargs):
        return ok(self._data(self.get_object()))

    def create(self, request, *args, **kwargs):
        serializer = self.get_serializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        account = services.create_account(data=serializer.validated_data, user=request.user)
        return created(self._data(account), f"Account {account.name} created.")

    def update(self, request, *args, **kwargs):
        partial = kwargs.pop("partial", False)
        instance = self.get_object()
        serializer = self.get_serializer(instance, data=request.data, partial=partial)
        serializer.is_valid(raise_exception=True)
        account = services.update_account(instance, data=dict(serializer.validated_data), user=request.user)
        return ok(self._data(account), f"Account {account.name} updated.")

    def destroy(self, request, *args, **kwargs):
        services.delete_account(self.get_object(), user=request.user)
        return ok(None, "Account deleted.")

    @action(detail=True, methods=["post"])
    def deactivate(self, request, pk=None):
        account = services.set_account_active(self.get_object(), active=False, user=request.user)
        return ok(self._data(account), f"{account.name} deactivated.")

    @action(detail=True, methods=["post"])
    def activate(self, request, pk=None):
        account = services.set_account_active(self.get_object(), active=True, user=request.user)
        return ok(self._data(account), f"{account.name} activated.")

    @action(detail=True, methods=["get"])
    def book(self, request, pk=None):
        """Cash/bank book: opening balance, every entry with a running balance, closing balance."""
        account = self.get_object()
        data = services.account_book(
            account, start=parse_date_param(request, "start_date"), end=parse_date_param(request, "end_date")
        )
        if request.query_params.get("export"):
            columns = [
                Column("date", "Date", width=0.8),
                Column("voucher_type", "Type", width=0.7),
                Column("number", "Voucher", width=0.9),
                Column("particulars", "Particulars", width=2.4),
                Column("reference_number", "Ref.", width=0.8),
                Column("money_in", "In", numeric=True),
                Column("money_out", "Out", numeric=True),
                Column("balance", "Balance", numeric=True),
            ]
            opening = {"date": data["start_date"] or "", "particulars": "Opening Balance", "balance": data["opening_balance"]}
            totals = {"particulars": "Closing Balance", "money_in": data["total_in"], "money_out": data["total_out"],
                      "balance": data["closing_balance"]}
            meta = [("Account", account.name), ("From", data["start_date"] or "Beginning"), ("To", data["end_date"])]
            response = export_response(
                request, base_name=f"account-book-{account.pk}", title=f"{account.get_kind_display()} Book", columns=columns,
                rows=[opening, *data["entries"]], meta=meta, totals=totals,
            )
            if response:
                return response
        return ok(data)


class ContraFilter(django_filters.FilterSet):
    start_date = django_filters.DateFilter(field_name="date", lookup_expr="gte")
    end_date = django_filters.DateFilter(field_name="date", lookup_expr="lte")
    account = django_filters.NumberFilter(method="filter_account")

    class Meta:
        model = Contra
        fields = ["status"]

    def filter_account(self, qs, name, value):
        return qs.filter(Q(from_account_id=value) | Q(to_account_id=value))


class ContraViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, mixins.CreateModelMixin, viewsets.GenericViewSet):
    """Contra entries are never edited or deleted — they are cancelled."""

    serializer_class = ContraSerializer
    permission_classes = [RolePermission]
    permission_map = {"read": "cashbank.view", "create": "contra.create", "cancel": "contra.cancel"}
    filterset_class = ContraFilter
    search_fields = ["contra_number", "reference_number", "notes", "from_account__name", "to_account__name"]
    ordering_fields = ["date", "contra_number", "amount"]
    ordering = ["-date", "-id"]

    def get_queryset(self):
        return Contra.objects.select_related("from_account", "to_account", "created_by", "cancelled_by")

    def create(self, request, *args, **kwargs):
        serializer = ContraInputSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        contra = services.create_contra(data=serializer.validated_data, user=request.user)
        contra = self.get_queryset().get(pk=contra.pk)
        return created(ContraSerializer(contra).data, f"Contra {contra.contra_number} recorded successfully.")

    @action(detail=True, methods=["post"])
    def cancel(self, request, pk=None):
        serializer = ReasonSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        contra = services.cancel_contra(self.get_object(), reason=serializer.validated_data["reason"], user=request.user)
        return ok(ContraSerializer(self.get_queryset().get(pk=contra.pk)).data, f"Contra {contra.contra_number} cancelled.")


DAY_BOOK_COLUMNS = [
    Column("date", "Date", width=0.8),
    Column("voucher_type", "Type", width=0.7),
    Column("number", "Voucher", width=0.9),
    Column("particulars", "Particulars", width=2.4),
    Column("account_name", "Cash/Bank", width=1.2),
    Column("amount", "Amount", numeric=True),
    Column("status", "Status", width=0.7),
]


class DayBookView(APIView):
    """Every voucher (sales, purchases, receipts, payments, expenses, contra) for a date range."""

    permission_classes = [RolePermission]
    permission_map = {"read": "reports.view"}

    def get(self, request):
        start = parse_date_param(request, "start_date") or timezone.localdate()
        end = parse_date_param(request, "end_date") or start
        data = services.day_book(start=start, end=end, include_cancelled=request.query_params.get("status") == "ALL")
        if request.query_params.get("export"):
            summary = [(t["voucher_type"], f"{t['amount']:,.2f}") for t in data["totals"]]
            response = export_response(
                request, base_name="day-book", title="Day Book", columns=DAY_BOOK_COLUMNS, rows=data["entries"],
                meta=[("From", start), ("To", end)], summary=summary or None,
            )
            if response:
                return response
        return ok(data)
