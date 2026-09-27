from decimal import Decimal

from rest_framework.renderers import JSONRenderer
from rest_framework.utils.encoders import JSONEncoder


class DecimalStringEncoder(JSONEncoder):
    """Serialise Decimals as exact strings — financial values never become floats."""

    def default(self, obj):
        if isinstance(obj, Decimal):
            return str(obj)
        return super().default(obj)


class EnvelopeJSONRenderer(JSONRenderer):
    """Wrap successful payloads as ``{"success": true, "data": ..., "message": ...}``.

    Payloads that already carry a ``success`` key (errors, or views that build
    their own envelope via :func:`apps.common.responses.ok`) pass through.
    """

    encoder_class = DecimalStringEncoder

    def render(self, data, accepted_media_type=None, renderer_context=None):
        response = (renderer_context or {}).get("response")
        if response is not None and response.status_code == 204:
            return b""
        if not (isinstance(data, dict) and "success" in data):
            failed = response is not None and response.status_code >= 400
            if failed:
                data = {"success": False, "error": {"code": "ERROR", "message": str(data)}}
            else:
                data = {"success": True, "data": data, "message": ""}
        return super().render(data, accepted_media_type, renderer_context)
