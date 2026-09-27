from django.db import transaction

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot
from apps.common.exceptions import BusinessError

from .models import Product


@transaction.atomic
def create_product(*, data: dict, user, opening_date=None) -> Product:
    product = Product.objects.create(**data)
    _post_opening_stock(product, user, opening_date)
    record(AuditAction.CREATE, product, after=snapshot(product), user=user)
    return product


def _post_opening_stock(product: Product, user, opening_date=None) -> None:
    # Imported lazily: the inventory app depends on products.
    try:
        from apps.inventory.services import post_opening_stock
    except ImportError:  # pragma: no cover - inventory app not installed
        return
    post_opening_stock(product, user=user, date=opening_date)


@transaction.atomic
def update_product(product: Product, *, data: dict, user) -> Product:
    product = Product.objects.select_for_update().get(pk=product.pk)
    before = snapshot(product)
    data.pop("opening_stock", None)
    for key, value in data.items():
        setattr(product, key, value)
    product.save()
    record(AuditAction.UPDATE, product, before=before, after=snapshot(product), user=user)
    return product


@transaction.atomic
def set_product_active(product: Product, *, active: bool, user) -> Product:
    before = snapshot(product)
    product.is_active = active
    product.save(update_fields=["is_active", "updated_at"])
    record(AuditAction.ACTIVATE if active else AuditAction.DEACTIVATE, product, before=before, after=snapshot(product), user=user)
    return product


def product_has_history(product: Product) -> bool:
    for rel in product._meta.related_objects:
        if rel.one_to_many or rel.one_to_one:
            accessor = rel.get_accessor_name()
            manager = getattr(product, accessor, None)
            if manager is not None and hasattr(manager, "exists") and manager.exists():
                return True
    return False


@transaction.atomic
def delete_product(product: Product, *, user) -> None:
    """Physically delete only products that were never used anywhere."""
    if product_has_history(product):
        raise BusinessError(
            "This product has transaction history and cannot be deleted. Deactivate it instead.",
            code="PRODUCT_HAS_HISTORY",
            status_code=409,
        )
    before = snapshot(product)
    record(AuditAction.DELETE, product, before=before, user=user)
    product.delete()
