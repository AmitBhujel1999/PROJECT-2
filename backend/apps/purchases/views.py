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
from .models import Purchase
from .serializers import CancelSerializer, PurchaseDetailSerializer, PurchaseInputSerializer, PurchaseListSerializer


class PurchaseFilter(django_filters.FilterSet):
    start_date = django_filters.DateFilter(field_name="date", lookup_expr="gte")
    end_date = django_filters.DateFilter(field_name="date", lookup_expr="lte")
    bill_number = django_filters.CharFilter(field_name="bill_number", lookup_expr="icontains")

    class Meta:
        model = Purchase
        fields = ["vendor", "payment_status", "status"]


class PurchaseViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, mixins.CreateModelMixin, viewsets.GenericViewSet):
    permission_classes = [RolePermission]
    permission_map = {
        "read": "purchases.view",
        "create": "purchases.create",
        "calculate": "purchases.create",
        "pdf": "purchases.view",
        "cancel": "purchases.cancel",
    }
    filterset_class = PurchaseFilter
    search_fields = ["bill_number", "vendor_bill_number", "vendor__name", "vendor__pan_vat_no"]
    ordering_fields = ["date", "bill_number", "total_amount", "due_date"]
    ordering = ["-date", "-id"]

    def get_queryset(self):
        qs = Purchase.objects.select_related("vendor", "created_by", "cancelled_by")
        if self.action == "list":
            qs = qs.annotate(item_count=Count("items"), total_quantity=Sum("items__quantity"))
        else:
            qs = qs.prefetch_related("items__product")
        return qs

    def get_serializer_class(self):
        return PurchaseListSerializer if self.action == "list" else PurchaseDetailSerializer

    def create(self, request, *args, **kwargs):
        serializer = PurchaseInputSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        purchase = services.create_purchase(data=serializer.validated_data, user=request.user)
        purchase = self.get_queryset().get(pk=purchase.pk)
        return created(PurchaseDetailSerializer(purchase).data, f"Purchase {purchase.bill_number} recorded successfully.")

    @action(detail=False, methods=["post"])
    def calculate(self, request):
        """Server-side preview of totals for the entry form (nothing is saved)."""
        serializer = PurchaseInputSerializer(data=request.data, context={"preview": True})
        serializer.is_valid(raise_exception=True)
        totals = services.calculate_purchase_totals(serializer.validated_data)
        return ok(serialize_totals(totals))

    @action(detail=True, methods=["post"])
    def cancel(self, request, pk=None):
        serializer = CancelSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        purchase = services.cancel_purchase(self.get_object(), reason=serializer.validated_data["reason"], user=request.user)
        purchase = self.get_queryset().get(pk=purchase.pk)
        return ok(PurchaseDetailSerializer(purchase).data, f"Purchase {purchase.bill_number} cancelled.")

    @action(detail=True, methods=["get"])
    def pdf(self, request, pk=None):
        """Printable PDF (``?inline=1`` opens in the browser for printing)."""
        doc = self.get_object()
        return pdf_response(
            trade_document_pdf(doc, kind="purchase"), f"{doc.bill_number}.pdf", inline=request.query_params.get("inline") == "1"
        )
