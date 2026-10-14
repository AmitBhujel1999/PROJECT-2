"""Bulk create/update from a grid (products, customers, vendors).

Every row is validated first with the model's own serializer; when any row is
wrong nothing is saved and the errors come back per row. Saving goes through
each module's service, so audit and business rules apply row by row.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field

from django.db import transaction
from rest_framework import serializers

from apps.common.exceptions import BusinessError

MAX_ROWS = 1000


@dataclass
class BulkConfig:
    serializer_class: type[serializers.Serializer]
    queryset: Callable  # () -> QuerySet of the rows this grid may change
    fields: list[str]
    create: Callable  # (data, user) -> obj
    update: Callable  # (obj, data, user) -> obj
    set_active: Callable  # (obj, active, user) -> obj
    defaults: dict = field(default_factory=dict)  # added to new rows
    unique: list[str] = field(default_factory=list)  # fields that must differ between rows (case-insensitive)


def _messages(errors) -> dict:
    out = {}
    for key, value in errors.items():
        if isinstance(value, (list, tuple)):
            value = value[0] if value else ""
        out[key] = str(value)
    return out


def bulk_save(cfg: BulkConfig, rows, *, user, context=None) -> dict:
    if not isinstance(rows, list) or not rows:
        raise BusinessError("Nothing to save.", code="EMPTY")
    if len(rows) > MAX_ROWS:
        raise BusinessError(f"Save at most {MAX_ROWS} rows at a time.", code="TOO_MANY_ROWS")

    existing = {obj.pk: obj for obj in cfg.queryset().filter(pk__in=[r.get("id") for r in rows if isinstance(r, dict) and r.get("id")])}
    errors: dict[int, dict] = {}
    plan = []  # (obj or None, validated data, active or None)
    seen: dict[str, dict[str, int]] = {name: {} for name in cfg.unique}

    for i, row in enumerate(rows):
        if not isinstance(row, dict):
            errors[i] = {"row": "This row could not be read."}
            continue
        obj = None
        if row.get("id"):
            obj = existing.get(row["id"])
            if obj is None:
                errors[i] = {"row": "This one was deleted by someone else. Reload the page."}
                continue
        data = {k: row[k] for k in cfg.fields if k in row}
        active = row.get("is_active")
        if obj is None:
            data = {**cfg.defaults, **data}
        ser = cfg.serializer_class(obj, data=data, partial=obj is not None, context=context or {})
        if not ser.is_valid():
            errors[i] = _messages(ser.errors)
            continue
        valid = dict(ser.validated_data)
        for name in cfg.unique:
            value = str(valid.get(name, getattr(obj, name, "") if obj else "")).strip().lower()
            if not value:
                continue
            if value in seen[name]:
                errors[i] = {name: "Used twice in this list."}
            else:
                seen[name][value] = i
        if obj is not None:
            valid = {k: v for k, v in valid.items() if getattr(obj, k) != v}
        plan.append((i, obj, valid, None if active is None else bool(active)))

    if errors:
        raise BusinessError(
            "Some rows have problems. Nothing was saved.",
            code="BULK_INVALID",
            details={"rows": {str(k): v for k, v in errors.items()}},
        )

    created = updated = 0
    with transaction.atomic():
        for _, obj, data, active in plan:
            if obj is None:
                obj = cfg.create(data, user)
                created += 1
                if active is False:
                    cfg.set_active(obj, False, user)
                continue
            changed = False
            if data:
                obj = cfg.update(obj, data, user)
                changed = True
            if active is not None and active != obj.is_active:
                cfg.set_active(obj, active, user)
                changed = True
            updated += changed
    return {"created": created, "updated": updated}


def summary_message(result: dict, noun: str) -> str:
    parts = []
    if result["created"]:
        n = result["created"]
        parts.append(f"{n} new {noun}{'' if n == 1 else 's'} added")
    if result["updated"]:
        n = result["updated"]
        parts.append(f"{n} {noun}{'' if n == 1 else 's'} changed")
    return ("Saved: " + ", ".join(parts) + ".") if parts else "Nothing changed."
