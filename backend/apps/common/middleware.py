class SecurityHeadersMiddleware:
    """Adds a restrictive CSP and related headers to API/admin responses.

    The API only ever returns JSON/CSV/PDF, so it never needs to load scripts.
    The Django admin needs its own static assets, so it gets a 'self' policy.
    The SvelteKit frontend's CSP is configured by SvelteKit + nginx.
    """

    API_CSP = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
    ADMIN_CSP = (
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; "
        "script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'"
    )

    def __init__(self, get_response):
        self.get_response = get_response

    def __call__(self, request):
        response = self.get_response(request)
        if request.path.startswith("/api/"):
            response.setdefault("Content-Security-Policy", self.API_CSP)
            response.setdefault("Cache-Control", "no-store")
        else:
            response.setdefault("Content-Security-Policy", self.ADMIN_CSP)
        response.setdefault("Permissions-Policy", "camera=(), microphone=(), geolocation=()")
        return response
