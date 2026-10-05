"""Several companies in one PostgreSQL database, one schema per company.

The first company lives in the ``public`` schema (slug ``main``); every company
added later gets its own schema (``co_<slug>``) holding a full copy of the
tables, its own users and its own sessions. Each request picks its company from
the ``acct_company`` cookie and sets the connection's ``search_path`` to that
schema only, so every query, transaction and migration stays inside it.

The list of extra companies is kept in ``public.acct_company`` and is always
queried with a schema-qualified name.
"""

from __future__ import annotations

import contextvars
import re
from dataclasses import dataclass

from django.core.management import call_command
from django.db import connection
from django.db.backends.signals import connection_created
from django.dispatch import receiver
from django.utils.text import slugify

COOKIE_NAME = "acct_company"
MAIN_SLUG = "main"
MAIN_SCHEMA = "public"
_SCHEMA_RE = re.compile(r"^(public|co_[a-z0-9_]{1,50})$")

_current_schema = contextvars.ContextVar("acct_company_schema", default=MAIN_SCHEMA)


@dataclass(frozen=True)
class Company:
    slug: str
    schema: str


MAIN = Company(MAIN_SLUG, MAIN_SCHEMA)


def _set_search_path(conn, schema: str) -> None:
    if not _SCHEMA_RE.match(schema):
        raise ValueError(f"Invalid company schema {schema!r}.")
    with conn.cursor() as cursor:
        cursor.execute(f"SET search_path TO {conn.ops.quote_name(schema)}")


@receiver(connection_created)
def _apply_schema_to_new_connection(sender, connection, **kwargs):
    if connection.vendor == "postgresql" and _current_schema.get() != MAIN_SCHEMA:
        _set_search_path(connection, _current_schema.get())


def activate(schema: str) -> None:
    """Point the default connection (now and when it reconnects) at ``schema``."""
    from django.contrib.contenttypes.models import ContentType

    if schema != _current_schema.get():
        ContentType.objects.clear_cache()  # ids differ between schemas
    _current_schema.set(schema)
    if connection.connection is not None:
        _set_search_path(connection, schema)


def current_schema() -> str:
    return _current_schema.get()


def _registry_rows() -> list[tuple[str, str, str]]:
    with connection.cursor() as cursor:
        cursor.execute("SELECT to_regclass('public.acct_company')")
        if cursor.fetchone()[0] is None:
            return []
        cursor.execute("SELECT slug, name, schema_name FROM public.acct_company ORDER BY created_at")
        return cursor.fetchall()


def get_company(slug: str | None) -> Company | None:
    if not slug or slug == MAIN_SLUG:
        return MAIN
    for row_slug, _name, schema in _registry_rows():
        if row_slug == slug:
            return Company(row_slug, schema)
    return None


def _business_name(schema: str, fallback: str) -> str:
    with connection.cursor() as cursor:
        cursor.execute(
            f"SELECT business_name FROM {connection.ops.quote_name(schema)}.common_businesssettings WHERE id = 1"
        )
        row = cursor.fetchone()
    return row[0] if row else fallback


def list_companies() -> list[dict]:
    companies = [{"slug": MAIN_SLUG, "name": _business_name(MAIN_SCHEMA, "Main company")}]
    for slug, name, schema in _registry_rows():
        companies.append({"slug": slug, "name": _business_name(schema, name)})
    return companies


def _unique_slug(name: str) -> str:
    base = slugify(name).replace("-", "_")[:40].strip("_") or "company"
    taken = {MAIN_SLUG, *(row[0] for row in _registry_rows())}
    slug, n = base, 2
    while slug in taken:
        slug = f"{base}_{n}"
        n += 1
    return slug


def migrate_schema(schema: str) -> None:
    previous = current_schema()
    activate(schema)
    try:
        call_command("migrate", interactive=False, verbosity=0)
    finally:
        activate(previous)


def create_company(*, name: str, details: dict, admin: dict) -> Company:
    """Create a schema, build all tables in it, save the company details and its first admin."""
    from apps.common.models import BusinessSettings
    from apps.users.models import Role, User

    slug = _unique_slug(name)
    schema = f"co_{slug}"
    quoted = connection.ops.quote_name(schema)
    with connection.cursor() as cursor:
        cursor.execute(f"CREATE SCHEMA {quoted}")
    previous = current_schema()
    try:
        migrate_schema(schema)
        activate(schema)
        settings = BusinessSettings.get_solo()
        settings.business_name = name
        for field, value in details.items():
            setattr(settings, field, value)
        settings.save()
        User.objects.create_superuser(
            username=admin["username"],
            email=admin.get("email", ""),
            password=admin["password"],
            role=Role.ADMIN,
            first_name=admin.get("first_name", ""),
        )
        with connection.cursor() as cursor:
            cursor.execute(
                "INSERT INTO public.acct_company (slug, name, schema_name) VALUES (%s, %s, %s)",
                [slug, name, schema],
            )
    except BaseException:
        activate(MAIN_SCHEMA)
        with connection.cursor() as cursor:
            cursor.execute(f"DROP SCHEMA IF EXISTS {quoted} CASCADE")
        raise
    finally:
        activate(previous)
    return Company(slug, schema)


class CompanyMiddleware:
    """Runs first: selects the company schema for the whole request."""

    def __init__(self, get_response):
        self.get_response = get_response

    def __call__(self, request):
        activate(MAIN_SCHEMA)
        company = get_company(request.COOKIES.get(COOKIE_NAME)) or MAIN
        request.company = company
        activate(company.schema)
        try:
            return self.get_response(request)
        finally:
            activate(MAIN_SCHEMA)
