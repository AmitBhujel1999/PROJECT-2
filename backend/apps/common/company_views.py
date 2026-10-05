from django.contrib.auth import authenticate, password_validation
from django.core.exceptions import ValidationError
from django.utils.decorators import method_decorator
from django.views.decorators.csrf import csrf_protect
from rest_framework import serializers
from rest_framework.permissions import AllowAny
from rest_framework.throttling import ScopedRateThrottle
from rest_framework.views import APIView

from apps.users.models import Role, User

from . import companies
from .exceptions import BusinessError
from .responses import created, ok


class CompanyCreateSerializer(serializers.Serializer):
    name = serializers.CharField(max_length=200)
    address = serializers.CharField(required=False, allow_blank=True, default="")
    pan_vat_no = serializers.CharField(max_length=30, required=False, allow_blank=True, default="")
    phone = serializers.CharField(max_length=30, required=False, allow_blank=True, default="")
    email = serializers.EmailField(required=False, allow_blank=True, default="")
    admin_name = serializers.CharField(max_length=150, required=False, allow_blank=True, default="")
    admin_username = serializers.CharField(max_length=150)
    admin_password = serializers.CharField(max_length=128, trim_whitespace=False)
    # An admin of an existing company must approve every new company.
    authorize_company = serializers.CharField(max_length=50)
    authorize_username = serializers.CharField(max_length=150)
    authorize_password = serializers.CharField(max_length=128, trim_whitespace=False)

    def validate_admin_username(self, value):
        try:
            User._meta.get_field("username").run_validators(value)
        except ValidationError as exc:
            raise serializers.ValidationError(exc.messages) from exc
        return value

    def validate_admin_password(self, value):
        try:
            password_validation.validate_password(value)
        except ValidationError as exc:
            raise serializers.ValidationError(exc.messages) from exc
        return value


@method_decorator(csrf_protect, name="dispatch")
class CompanyListView(APIView):
    """Companies shown on the login screen, and creation of a new, empty company."""

    permission_classes = [AllowAny]
    throttle_scope = "login"

    def get_throttles(self):
        return [ScopedRateThrottle()] if self.request.method == "POST" else super().get_throttles()

    def get(self, request):
        return ok({"companies": companies.list_companies(), "current": request.company.slug})

    def post(self, request):
        serializer = CompanyCreateSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        data = serializer.validated_data

        source = companies.get_company(data["authorize_company"])
        if source is None:
            raise BusinessError("Unknown company.", code="UNKNOWN_COMPANY", status_code=400)
        companies.activate(source.schema)
        approver = authenticate(request, username=data["authorize_username"], password=data["authorize_password"])
        companies.activate(request.company.schema)
        if approver is None or not approver.is_active or approver.role != Role.ADMIN:
            raise BusinessError(
                "An admin username and password of the selected company is required.",
                code="INVALID_CREDENTIALS",
                status_code=400,
            )

        company = companies.create_company(
            name=data["name"].strip(),
            details={k: data[k] for k in ("address", "pan_vat_no", "phone", "email")},
            admin={
                "username": data["admin_username"],
                "password": data["admin_password"],
                "email": data["email"],
                "first_name": data["admin_name"],
            },
        )
        return created({"slug": company.slug, "name": data["name"].strip()}, "Company created.")
