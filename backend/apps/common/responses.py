from rest_framework import status as http_status
from rest_framework.response import Response


def ok(data=None, message: str = "", status: int = http_status.HTTP_200_OK) -> Response:
    return Response({"success": True, "data": data, "message": message}, status=status)


def created(data=None, message: str = "Created successfully.") -> Response:
    return ok(data, message, http_status.HTTP_201_CREATED)
