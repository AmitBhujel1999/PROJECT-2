"""Request-scoped audit context (user + IP) available to the service layer."""

from contextvars import ContextVar
from dataclasses import dataclass


@dataclass
class AuditContext:
    user: object | None = None
    ip_address: str | None = None


_current: ContextVar[AuditContext | None] = ContextVar("audit_context", default=None)


def get_context() -> AuditContext:
    return _current.get() or AuditContext()


def set_context(ctx: AuditContext):
    return _current.set(ctx)


def reset_context(token) -> None:
    _current.reset(token)
