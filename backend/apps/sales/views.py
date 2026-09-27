import django_filters
from django.db.models import Count, Sum
from rest_framework import mixins, viewsets
from rest_framework.decorators import action

from apps.common.documents import serialize_totals
from apps.reports.documents_pdf import trade_document_pdf
from apps.reports.exporters import pdf_response
from apps.common.responses import created, ok
from apps.users.permissions import RolePermission

from . import services
from .models import Sale
from .serializers import CancelSerializer, SaleDetailSerializer, SaleInputSerializer, SaleListSerializer


class SaleFilter(django_filters.FilterSet):
    start_date = django_filters.DateFilter(field_name="date", lookup_expr="gte")
    end_date = django_filters.DateFilter(field_name="date", lookup_expr="lte")
    invoice_number = django_filters.CharFilter(field_name="invoice_number", lookup_expr="icontains")

    class Meta:
        model = Sale
        fields = ["customer", "payment_status", "status"]


class SaleViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, mixins.CreateModelMixin, viewsets.GenericViewSet):
    permission_classes = [RolePermission]
    permission_map = {
        "read": "sales.view",
        "create": "sales.create",
        "calculate": "sales.create",
        "pdf": "sales.view",
        "cancel": "sales.cancel",
    }
    filterset_class = SaleFilter
    search_fields = ["invoice_number", "customer__name", "customer__pan_vat_no", "customer__phone"]
    ordering_fields = ["date", "invoice_number", "total_amount", "due_date"]
    ordering = ["-date", "-id"]

    def get_queryset(self):
        qs = Sale.objects.select_related("customer", "created_by", "cancelled_by")
        if self.action == "list":
            qs = qs.annotate(item_count=Count("items"), total_quantity=Sum("items__quantity"))
        else:
            qs = qs.prefetch_related("items__product")
        return qs

    def get_serializer_class(self):
        return SaleListSerializer if self.action == "list" else SaleDetailSerializer

    def create(self, request, *args, **kwargs):
        serializer = SaleInputSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        sale, warnings = services.create_sale(data=serializer.validated_data, user=request.user)
        data = SaleDetailSerializer(self.get_queryset().get(pk=sale.pk)).data
        data["warnings"] = warnings
        return created(data, f"Sale {sale.invoice_number} created successfully.")

    @action(detail=False, methods=["post"])
    def calculate(self, request):
        """Server-side preview of totals and available stock (nothing is saved)."""
        serializer = SaleInputSerializer(data=request.data, context={"preview": True})
        serializer.is_valid(raise_exception=True)
        totals, stock = services.calculate_sale_totals(serializer.validated_data)
        return ok(serialize_totals(totals, stock))

    @action(detail=True, methods=["post"])
    def cancel(self, request, pk=None):
        serializer = CancelSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        sale = services.cancel_sale(self.get_object(), reason=serializer.validated_data["reason"], user=request.user)
        sale = self.get_queryset().get(pk=sale.pk)
        return ok(SaleDetailSerializer(sale).data, f"Sale {sale.invoice_number} cancelled.")

    @action(detail=True, methods=["get"])
    def pdf(self, request, pk=None):
        """Printable PDF (``?inline=1`` opens in the browser for printing)."""
        doc = self.get_object()
        return pdf_response(
            trade_document_pdf(doc, kind="sale"), f"{doc.invoice_number}.pdf", inline=request.query_params.get("inline") == "1"
        )
