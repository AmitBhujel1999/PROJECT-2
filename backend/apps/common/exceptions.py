"""Uniform API error handling.

Every error leaves the API as::

    {"success": false, "error": {"code": "...", "message": "...", "details": {...}}}
"""

from __future__ import annotations

import logging

from django.core.exceptions import PermissionDenied as DjangoPermissionDenied
from django.core.exceptions import ValidationError as DjangoValidationError
from django.db.models import ProtectedError
from django.http import Http404
from rest_framework import exceptions, status
from rest_framework.response import Response
from rest_framework.views import exception_handler

logger = logging.getLogger(__name__)


class BusinessError(Exception):
    """A domain-rule violation raised by the service layer."""

    status_code = status.HTTP_400_BAD_REQUEST
    default_code = "BUSINESS_RULE_VIOLATION"

    def __init__(self, message: str, code: str | None = None, details: dict | None = None, status_code: int | None = None):
        super().__init__(message)
        self.message = message
        self.code = code or self.default_code
        self.details = details or {}
        if status_code:
            self.status_code = status_code


class InsufficientStockError(BusinessError):
    default_code = "INSUFFICIENT_STOCK"
    status_code = status.HTTP_409_CONFLICT


class AllocationError(BusinessError):
    default_code = "INVALID_ALLOCATION"


def _first_message(detail) -> str:
    if isinstance(detail, list) and detail:
        return _first_message(detail[0])
    if isinstance(detail, dict) and detail:
        key, value = next(iter(detail.items()))
        msg = _first_message(value)
        if key in ("non_field_errors", "__all__", "detail"):
            return msg
        return f"{key.replace('_', ' ').capitalize()}: {msg}"
    return str(detail)


def _error(code: str, message: str, http_status: int, details=None, headers=None) -> Response:
    body = {"success": False, "error": {"code": code, "message": message}}
    if details:
        body["error"]["details"] = details
    return Response(body, status=http_status, headers=headers)


def api_exception_handler(exc, context):
    if isinstance(exc, BusinessError):
        return _error(exc.code, exc.message, exc.status_code, exc.details)
    if isinstance(exc, DjangoValidationError):
        detail = exc.message_dict if hasattr(exc, "error_dict") else {"non_field_errors": exc.messages}
        return _error("VALIDATION_ERROR", _first_message(detail), 400, detail)
    if isinstance(exc, ProtectedError):
        return _error(
            "PROTECTED",
            "This record is referenced by other records and cannot be deleted. Deactivate it instead.",
            status.HTTP_409_CONFLICT,
        )
    if isinstance(exc, Http404):
        exc = exceptions.NotFound()
    if isinstance(exc, DjangoPermissionDenied):
        exc = exceptions.PermissionDenied()

    response = exception_handler(exc, context)
    if response is None:
        logger.exception("Unhandled API error", exc_info=exc)
        return _error("SERVER_ERROR", "An unexpected error occurred.", 500)

    headers = {k: v for k, v in response.headers.items() if k in ("Retry-After", "WWW-Authenticate", "Allow")}
    if isinstance(exc, exceptions.ValidationError):
        return _error("VALIDATION_ERROR", _first_message(exc.detail), 400, exc.detail, headers)
    if isinstance(exc, exceptions.NotAuthenticated | exceptions.AuthenticationFailed):
        return _error("AUTHENTICATION_REQUIRED", _first_message(exc.detail), 401, headers=headers)
    if isinstance(exc, exceptions.PermissionDenied):
        return _error("PERMISSION_DENIED", _first_message(exc.detail), 403, headers=headers)
    if isinstance(exc, exceptions.NotFound):
        return _error("NOT_FOUND", "The requested record was not found.", 404, headers=headers)
    if isinstance(exc, exceptions.Throttled):
        return _error("RATE_LIMITED", _first_message(exc.detail), 429, headers=headers)
    if isinstance(exc, exceptions.MethodNotAllowed):
        return _error("METHOD_NOT_ALLOWED", _first_message(exc.detail), 405, headers=headers)
    code = getattr(exc, "default_code", "ERROR").upper()
    return _error(code, _first_message(response.data), response.status_code, headers=headers)
