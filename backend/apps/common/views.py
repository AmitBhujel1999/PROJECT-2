from django.http import JsonResponse


def csrf_failure(request, reason=""):
    return JsonResponse(
        {"success": False, "error": {"code": "CSRF_FAILED", "message": "CSRF verification failed. Refresh the page and try again."}},
        status=403,
    )
