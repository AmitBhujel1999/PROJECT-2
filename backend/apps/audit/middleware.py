from apps.common.utils import client_ip

from .context import AuditContext, reset_context, set_context


class AuditContextMiddleware:
    def __init__(self, get_response):
        self.get_response = get_response

    def __call__(self, request):
        ctx = AuditContext(user=None, ip_address=client_ip(request))
        token = set_context(ctx)
        request.audit_context = ctx
        try:
            # DRF authenticates lazily; views refresh ctx.user via record()/request.user.
            user = getattr(request, "user", None)
            if user is not None and user.is_authenticated:
                ctx.user = user
            return self.get_response(request)
        finally:
            reset_context(token)
