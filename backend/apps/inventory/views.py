from decimal import Decimal

from django.db.models import Count, Q, Sum, Value
from django.db.models.functions import Coalesce
from django.shortcuts import get_object_or_404
from django.utils import timezone
from rest_framework import mixins, viewsets
from rest_framework.views import APIView

from apps.common.pagination import StandardPagination
from apps.common.responses import created, ok
from apps.common.utils import parse_date_param, parse_int_param
from apps.products.models import Product
from apps.users.permissions import RolePermission

from . import services
from .models import StockAdjustment
from .selectors import MONEY, ledger_row, stock_ledger, stock_register
from .serializers import StockAdjustmentCreateSerializer, StockAdjustmentSerializer, StockRegisterSerializer

REGISTER_ORDERING = {
    "name", "-name", "sku_code", "-sku_code", "current_stock", "-current_stock", "cost_value", "-cost_value",
    "retail_value", "-retail_value", "total_sold", "-total_sold", "total_purchased", "-total_purchased",
}


def register_queryset(request):
    as_of = parse_date_param(request, "as_of") or parse_date_param(request, "end_date")
    start = parse_date_param(request, "start_date")
    qs = Product.objects.all()
    search = request.query_params.get("search")
    if search:
        qs = qs.filter(Q(name__icontains=search) | Q(sku_code__icontains=search))
    active = request.query_params.get("is_active")
    if active in ("true", "false"):
        qs = qs.filter(is_active=active == "true")
    product_id = parse_int_param(request, "product")
    if product_id:
        qs = qs.filter(pk=product_id)
    qs = stock_register(qs, as_of=as_of, start=start)
    status = request.query_params.get("status")
    if status:
        qs = qs.filter(stock_status=status)
    ordering = request.query_params.get("ordering", "name")
    return qs.order_by(ordering if ordering in REGISTER_ORDERING else "name", "pk"), as_of, start


class StockRegisterView(APIView):
    """GET /api/inventory/ — stock register (current, as of a date, or for a period)."""

    permission_classes = [RolePermission]
    permission_map = {"read": "inventory.view"}

    def get(self, request):
        qs, as_of, start = register_queryset(request)
        summary = qs.order_by().aggregate(
            total_products=Count("pk"),
            total_cost_value=Coalesce(Sum("cost_value"), Value(Decimal("0")), output_field=MONEY),
            total_retail_value=Coalesce(Sum("retail_value"), Value(Decimal("0")), output_field=MONEY),
            low_stock_count=Count("pk", filter=Q(stock_status="LOW_STOCK")),
            out_of_stock_count=Count("pk", filter=Q(stock_status="OUT_OF_STOCK")),
        )
        paginator = StandardPagination()
        page = paginator.paginate_queryset(qs, request, view=self)
        data = StockRegisterSerializer(page, many=True).data
        return paginator.get_paginated_response(
            data,
            extra={
                "summary": {
                    **summary,
                    "as_of": as_of,
                    "start_date": start,
                }
            },
        )


class ProductStockView(APIView):
    """GET /api/inventory/<product_id>/ — current, as-of and between-dates stock."""

    permission_classes = [RolePermission]
    permission_map = {"read": "inventory.view"}

    def get(self, request, product_id: int):
        product = get_object_or_404(Product, pk=product_id)
        as_of = parse_date_param(request, "as_of")
        start = parse_date_param(request, "start_date")
        end = parse_date_param(request, "end_date") or timezone.localdate()
        data = {
            "product_id": product.pk,
            "name": product.name,
            "sku_code": product.sku_code,
            "unit": product.get_unit_display(),
            "reorder_level": product.reorder_level,
            "current_stock": services.calculate_stock(product),
        }
        if as_of:
            data["as_of"] = as_of
            data["stock_as_of"] = services.calculate_historical_stock(product, as_of)
        if start:
            data["period"] = {"start_date": start, "end_date": end, **services.calculate_stock_between(product, start, end)}
        return ok(data)


class StockLedgerView(APIView):
    """GET /api/inventory/ledger/ — immutable stock ledger with opening/closing quantities."""

    permission_classes = [RolePermission]
    permission_map = {"read": "inventory.view"}

    def get(self, request):
        qs = ledger_queryset(request)
        paginator = StandardPagination()
        page = paginator.paginate_queryset(qs, request, view=self)
        return paginator.get_paginated_response([ledger_row(r) for r in page])


def ledger_queryset(request):
    return stock_ledger(
        product_id=parse_int_param(request, "product"),
        transaction_type=request.query_params.get("transaction_type") or None,
        start=parse_date_param(request, "start_date"),
        end=parse_date_param(request, "end_date"),
        search=request.query_params.get("search") or None,
    )


class StockAdjustmentViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, mixins.CreateModelMixin, viewsets.GenericViewSet):
    permission_classes = [RolePermission]
    permission_map = {"read": "inventory.view", "write": "inventory.adjust"}
    serializer_class = StockAdjustmentSerializer
    filterset_fields = ["product", "reason"]
    search_fields = ["adjustment_number", "product__name", "product__sku_code", "notes"]
    ordering_fields = ["date", "adjustment_number", "quantity"]

    def get_queryset(self):
        qs = StockAdjustment.objects.select_related("product", "created_by")
        start = parse_date_param(self.request, "start_date")
        end = parse_date_param(self.request, "end_date")
        if start:
            qs = qs.filter(date__gte=start)
        if end:
            qs = qs.filter(date__lte=end)
        return qs

    def create(self, request, *args, **kwargs):
        serializer = StockAdjustmentCreateSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        data = serializer.validated_data
        adjustment = services.create_stock_adjustment(
            product_id=data["product"].pk,
            quantity=data["quantity"],
            reason=data["reason"],
            date=data["date"],
            notes=data.get("notes", ""),
            user=request.user,
        )
        return created(
            StockAdjustmentSerializer(adjustment).data,
            f"Stock adjustment {adjustment.adjustment_number} recorded for {adjustment.product.name}.",
        )
