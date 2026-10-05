import django_filters
from django.db.models import Count
from rest_framework import mixins, viewsets
from rest_framework.decorators import action

from apps.common.responses import created, ok
from apps.reports.documents_pdf import expense_pdf
from apps.reports.exporters import pdf_response
from apps.users.permissions import RolePermission

from . import services
from .models import Expense, ExpenseCategory
from .serializers import (
    CancelSerializer,
    ExpenseCalculateSerializer,
    ExpenseCategorySerializer,
    ExpenseDetailSerializer,
    ExpenseInputSerializer,
    ExpenseListSerializer,
)


class ExpenseCategoryViewSet(viewsets.ModelViewSet):
    serializer_class = ExpenseCategorySerializer
    permission_classes = [RolePermission]
    permission_map = {"read": "expenses.view", "write": "expenses.manage_categories"}
    filterset_fields = ["is_active"]
    search_fields = ["name", "description"]
    ordering_fields = ["name", "created_at"]
    ordering = ["name"]

    def get_queryset(self):
        return ExpenseCategory.objects.annotate(expense_count=Count("expenses"))

    def create(self, request, *args, **kwargs):
        serializer = self.get_serializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        category = services.create_category(data=serializer.validated_data, user=request.user)
        category = self.get_queryset().get(pk=category.pk)
        return created(self.get_serializer(category).data, f"Category {category.name} created successfully.")

    def update(self, request, *args, **kwargs):
        partial = kwargs.pop("partial", False)
        instance = self.get_object()
        serializer = self.get_serializer(instance, data=request.data, partial=partial)
        serializer.is_valid(raise_exception=True)
        services.update_category(instance, data=dict(serializer.validated_data), user=request.user)
        category = self.get_queryset().get(pk=instance.pk)
        return ok(self.get_serializer(category).data, f"Category {category.name} updated successfully.")

    def destroy(self, request, *args, **kwargs):
        services.delete_category(self.get_object(), user=request.user)
        return ok(None, "Category deleted.")

    @action(detail=True, methods=["post"])
    def deactivate(self, request, pk=None):
        category = services.set_category_active(self.get_object(), active=False, user=request.user)
        return ok(self.get_serializer(self.get_queryset().get(pk=category.pk)).data, f"{category.name} deactivated.")

    @action(detail=True, methods=["post"])
    def activate(self, request, pk=None):
        category = services.set_category_active(self.get_object(), active=True, user=request.user)
        return ok(self.get_serializer(self.get_queryset().get(pk=category.pk)).data, f"{category.name} activated.")


class ExpenseFilter(django_filters.FilterSet):
    start_date = django_filters.DateFilter(field_name="date", lookup_expr="gte")
    end_date = django_filters.DateFilter(field_name="date", lookup_expr="lte")

    class Meta:
        model = Expense
        fields = ["category", "vendor", "payment_method", "status"]


class ExpenseViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, mixins.CreateModelMixin, viewsets.GenericViewSet):
    """Expenses are never edited or deleted — they are cancelled."""

    permission_classes = [RolePermission]
    permission_map = {
        "read": "expenses.view",
        "create": "expenses.create",
        "calculate": "expenses.create",
        "pdf": "expenses.view",
        "cancel": "expenses.cancel",
    }
    filterset_class = ExpenseFilter
    search_fields = ["expense_number", "description", "payee", "reference_number", "vendor__name", "category__name"]
    ordering_fields = ["date", "expense_number", "total_amount"]
    ordering = ["-date", "-id"]

    def get_queryset(self):
        return Expense.objects.select_related("category", "vendor", "created_by", "cancelled_by")

    def get_serializer_class(self):
        return ExpenseListSerializer if self.action == "list" else ExpenseDetailSerializer

    def create(self, request, *args, **kwargs):
        serializer = ExpenseInputSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        expense = services.create_expense(data=serializer.validated_data, user=request.user)
        expense = self.get_queryset().get(pk=expense.pk)
        return created(ExpenseDetailSerializer(expense).data, f"Expense {expense.expense_number} recorded successfully.")

    @action(detail=False, methods=["post"])
    def calculate(self, request):
        """Server-side preview of tax and total for the entry form (nothing is saved)."""
        serializer = ExpenseCalculateSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        totals = services.calculate_expense(serializer.validated_data["amount"], serializer.validated_data["tax_rate"])
        return ok({k: str(v) for k, v in totals.items()})

    @action(detail=True, methods=["post"])
    def cancel(self, request, pk=None):
        serializer = CancelSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        expense = services.cancel_expense(self.get_object(), reason=serializer.validated_data["reason"], user=request.user)
        expense = self.get_queryset().get(pk=expense.pk)
        return ok(ExpenseDetailSerializer(expense).data, f"Expense {expense.expense_number} cancelled.")

    @action(detail=True, methods=["get"])
    def pdf(self, request, pk=None):
        """Printable expense voucher (``?inline=1`` opens in the browser)."""
        expense = self.get_object()
        return pdf_response(expense_pdf(expense), f"{expense.expense_number}.pdf", inline=request.query_params.get("inline") == "1")
