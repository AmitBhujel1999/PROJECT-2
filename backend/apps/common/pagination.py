import math

from rest_framework.pagination import PageNumberPagination
from rest_framework.response import Response


class StandardPagination(PageNumberPagination):
    """Server-side pagination: default 25 rows, page_size 25/50/100 allowed."""

    page_size = 25
    page_size_query_param = "page_size"
    max_page_size = 100
    allowed_sizes = (25, 50, 100)

    def get_page_size(self, request):
        size = super().get_page_size(request)
        if size not in self.allowed_sizes:
            return self.page_size
        return size

    def get_paginated_response(self, data, extra: dict | None = None):
        page_size = self.page.paginator.per_page
        count = self.page.paginator.count
        payload = {
            "results": data,
            "count": count,
            "page": self.page.number,
            "page_size": page_size,
            "total_pages": max(1, math.ceil(count / page_size)) if page_size else 1,
        }
        if extra:
            payload.update(extra)
        return Response({"success": True, "data": payload, "message": ""})


def paginate_list(request, rows: list, view=None, extra: dict | None = None):
    """Paginate an in-memory list (used by computed reports such as ledgers)."""
    paginator = StandardPagination()
    page = paginator.paginate_queryset(rows, request, view=view)
    return paginator.get_paginated_response(page, extra=extra)
