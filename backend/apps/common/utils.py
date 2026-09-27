from django.conf import settings


def client_ip(request) -> str | None:
    if request is None:
        return None
    if getattr(settings, "USE_X_FORWARDED_FOR", False):
        forwarded = request.META.get("HTTP_X_FORWARDED_FOR")
        if forwarded:
            return forwarded.split(",")[0].strip()
    return request.META.get("REMOTE_ADDR")


def parse_date_param(request, name: str, default=None):
    """Parse an ISO date query parameter or raise a VALIDATION_ERROR."""
    import datetime as dt

    from rest_framework.exceptions import ValidationError

    raw = request.query_params.get(name)
    if not raw:
        return default
    try:
        return dt.date.fromisoformat(raw)
    except ValueError as exc:
        raise ValidationError({name: "Enter a valid date (YYYY-MM-DD)."}) from exc


def parse_int_param(request, name: str):
    from rest_framework.exceptions import ValidationError

    raw = request.query_params.get(name)
    if raw in (None, ""):
        return None
    try:
        return int(raw)
    except ValueError as exc:
        raise ValidationError({name: "Must be a whole number."}) from exc
