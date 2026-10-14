import django_filters
from rest_framework import viewsets
from rest_framework.decorators import action

from apps.common.bulk import BulkConfig, bulk_save, summary_message
from apps.common.responses import created, ok
from apps.users.permissions import RolePermission

from . import services
from .models import Product
from .serializers import ProductSerializer


class ProductFilter(django_filters.FilterSet):
    is_active = django_filters.BooleanFilter()
    unit = django_filters.CharFilter()

    class Meta:
        model = Product
        fields = ["is_active", "unit"]


class ProductViewSet(viewsets.ModelViewSet):
    serializer_class = ProductSerializer
    permission_classes = [RolePermission]
    permission_map = {"read": "products.view", "write": "products.manage"}
    filterset_class = ProductFilter
    search_fields = ["name", "sku_code"]
    ordering_fields = ["name", "sku_code", "selling_price", "purchase_price", "created_at", "current_stock"]
    ordering = ["name"]

    def get_queryset(self):
        qs = Product.objects.all()
        try:
            from apps.inventory.selectors import annotate_current_stock
        except ImportError:  # pragma: no cover
            return qs
        return annotate_current_stock(qs)

    def create(self, request, *args, **kwargs):
        serializer = self.get_serializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        product = services.create_product(data=serializer.validated_data, user=request.user)
        product = self.get_queryset().get(pk=product.pk)
        return created(self.get_serializer(product).data, f"Product {product.name} created successfully.")

    def update(self, request, *args, **kwargs):
        partial = kwargs.pop("partial", False)
        instance = self.get_object()
        serializer = self.get_serializer(instance, data=request.data, partial=partial)
        serializer.is_valid(raise_exception=True)
        services.update_product(instance, data=dict(serializer.validated_data), user=request.user)
        product = self.get_queryset().get(pk=instance.pk)
        return ok(self.get_serializer(product).data, f"Product {product.name} updated successfully.")

    def destroy(self, request, *args, **kwargs):
        instance = self.get_object()
        services.delete_product(instance, user=request.user)
        return ok(None, "Product deleted.")

    @action(detail=True, methods=["post"])
    def deactivate(self, request, pk=None):
        product = services.set_product_active(self.get_object(), active=False, user=request.user)
        return ok(self.get_serializer(self.get_queryset().get(pk=product.pk)).data, f"{product.name} deactivated.")

    @action(detail=True, methods=["post"])
    def activate(self, request, pk=None):
        product = services.set_product_active(self.get_object(), active=True, user=request.user)
        return ok(self.get_serializer(self.get_queryset().get(pk=product.pk)).data, f"{product.name} activated.")

    @action(detail=False, methods=["post"])
    def bulk(self, request):
        """Create/update many products from the bulk-edit grid: {"rows": [{id?, field: value, ...}]}."""
        cfg = BulkConfig(
            serializer_class=ProductSerializer,
            queryset=Product.objects.all,
            fields=["name", "sku_code", "unit", "description", "purchase_price", "selling_price", "tax_rate", "reorder_level"],
            create=lambda data, user: services.create_product(data=data, user=user),
            update=lambda obj, data, user: services.update_product(obj, data=data, user=user),
            set_active=lambda obj, active, user: services.set_product_active(obj, active=active, user=user),
            unique=["sku_code"],
        )
        result = bulk_save(cfg, request.data.get("rows"), user=request.user, context=self.get_serializer_context())
        return ok(result, summary_message(result, "item"))
