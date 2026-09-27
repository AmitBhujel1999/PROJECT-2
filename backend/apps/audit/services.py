from __future__ import annotations

import json
from decimal import Decimal

from django.core.serializers.json import DjangoJSONEncoder
from django.db import models
from django.forms.models import model_to_dict

from .context import get_context
from .models import AuditLog

SENSITIVE_FIELDS = {"password", "last_login"}


def snapshot(instance: models.Model | None, exclude: set[str] | None = None) -> dict | None:
    """JSON-safe dict of a model instance's concrete fields."""
    if instance is None:
        return None
    exclude = SENSITIVE_FIELDS | (exclude or set())
    fields = [f.name for f in instance._meta.concrete_fields if f.name not in exclude]
    data = model_to_dict(instance, fields=fields)
    for f in instance._meta.concrete_fields:
        if f.name in data and isinstance(f, models.ForeignKey):
            data[f.name] = getattr(instance, f.attname)
    return json.loads(json.dumps(data, cls=DjangoJSONEncoder))


def record(
    action: str,
    instance: models.Model | None = None,
    *,
    model_name: str | None = None,
    object_id=None,
    object_repr: str | None = None,
    before: dict | None = None,
    after: dict | None = None,
    user=None,
    ip_address: str | None = None,
) -> AuditLog:
    ctx = get_context()
    user = user if user is not None else ctx.user
    if user is not None and not getattr(user, "is_authenticated", False):
        user = None
    if instance is not None:
        model_name = model_name or instance._meta.label
        object_id = object_id if object_id is not None else instance.pk
        object_repr = object_repr or str(instance)
    return AuditLog.objects.create(
        user=user,
        username=getattr(user, "username", "") if user else "",
        action=action,
        model_name=model_name or "",
        object_id="" if object_id is None else str(object_id),
        object_repr=(object_repr or "")[:255],
        ip_address=ip_address or ctx.ip_address,
        before_data=_clean(before),
        after_data=_clean(after),
    )


def _clean(data):
    if data is None:
        return None
    return json.loads(json.dumps(data, cls=DjangoJSONEncoder, default=lambda o: str(o) if isinstance(o, Decimal) else repr(o)))
