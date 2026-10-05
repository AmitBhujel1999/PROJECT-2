from django.conf import settings
from django.contrib.auth import authenticate, login, logout, update_session_auth_hash
from django.contrib.auth.tokens import default_token_generator
from django.core.mail import send_mail
from django.db import transaction
from django.utils.encoding import force_bytes, force_str
from django.utils.http import urlsafe_base64_decode, urlsafe_base64_encode
from django.views.decorators.csrf import csrf_protect, ensure_csrf_cookie
from django.utils.decorators import method_decorator
from django.contrib.auth import password_validation
from rest_framework import mixins, viewsets
from rest_framework.decorators import action
from rest_framework.permissions import AllowAny, IsAuthenticated
from rest_framework.throttling import ScopedRateThrottle
from rest_framework.views import APIView

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot
from apps.common import companies
from apps.common.exceptions import BusinessError
from apps.common.responses import created, ok

from .models import Role, User
from .permissions import PERMISSION_MATRIX, ROLE_DESCRIPTIONS, RolePermission
from .serializers import (
    AdminUserSerializer,
    LoginSerializer,
    PasswordChangeSerializer,
    PasswordResetConfirmSerializer,
    PasswordResetRequestSerializer,
    UserSerializer,
)


@method_decorator(ensure_csrf_cookie, name="dispatch")
class CsrfView(APIView):
    """Issues the CSRF cookie the SPA must echo back in the X-CSRFToken header."""

    permission_classes = [AllowAny]

    def get(self, request):
        return ok({"csrf": "set"})


@method_decorator(csrf_protect, name="dispatch")
class LoginView(APIView):
    permission_classes = [AllowAny]
    throttle_classes = [ScopedRateThrottle]
    throttle_scope = "login"

    def post(self, request):
        serializer = LoginSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        credentials = dict(serializer.validated_data)
        company = request.company
        if "company" in credentials:
            company = companies.get_company(credentials.pop("company"))
            if company is None:
                raise BusinessError("Unknown company.", code="UNKNOWN_COMPANY", status_code=400)
            companies.activate(company.schema)
        user = authenticate(request, **credentials)
        if user is None or not user.is_active:
            record(
                AuditAction.LOGIN_FAILED,
                model_name="users.User",
                object_repr=serializer.validated_data["username"],
            )
            raise BusinessError("Invalid username or password.", code="INVALID_CREDENTIALS", status_code=400)
        login(request, user)  # rotates the session key
        record(AuditAction.LOGIN, user, user=user)
        response = ok(UserSerializer(user).data, "Logged in successfully.")
        response.set_cookie(
            companies.COOKIE_NAME,
            company.slug,
            max_age=365 * 24 * 60 * 60,
            httponly=True,
            samesite="Lax",
            secure=settings.SESSION_COOKIE_SECURE,
        )
        return response


class LogoutView(APIView):
    permission_classes = [IsAuthenticated]

    def post(self, request):
        record(AuditAction.LOGOUT, request.user, user=request.user)
        logout(request)
        return ok(None, "Logged out.")


class MeView(APIView):
    permission_classes = [IsAuthenticated]

    def get(self, request):
        return ok(UserSerializer(request.user).data)

    def patch(self, request):
        before = snapshot(request.user)
        serializer = UserSerializer(request.user, data=request.data, partial=True)
        serializer.is_valid(raise_exception=True)
        serializer.save()
        record(AuditAction.UPDATE, request.user, before=before, after=snapshot(request.user))
        return ok(serializer.data, "Profile updated.")


class PasswordChangeView(APIView):
    permission_classes = [IsAuthenticated]

    def post(self, request):
        serializer = PasswordChangeSerializer(data=request.data, context={"request": request})
        serializer.is_valid(raise_exception=True)
        request.user.set_password(serializer.validated_data["new_password"])
        request.user.save(update_fields=["password"])
        update_session_auth_hash(request, request.user)
        record(AuditAction.PASSWORD_CHANGE, request.user)
        return ok(None, "Password changed successfully.")


@method_decorator(csrf_protect, name="dispatch")
class PasswordResetRequestView(APIView):
    permission_classes = [AllowAny]
    throttle_classes = [ScopedRateThrottle]
    throttle_scope = "password_reset"

    def post(self, request):
        serializer = PasswordResetRequestSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        email = serializer.validated_data["email"]
        for user in User.objects.filter(email__iexact=email, is_active=True):
            uid = urlsafe_base64_encode(force_bytes(user.pk))
            token = default_token_generator.make_token(user)
            link = f"{settings.FRONTEND_URL}/reset-password?uid={uid}&token={token}"
            send_mail(
                "Password reset request",
                f"Hello {user.display_name},\n\nUse the link below to reset your password:\n{link}\n\n"
                "If you did not request this, you can ignore this email.",
                settings.DEFAULT_FROM_EMAIL,
                [user.email],
                fail_silently=True,
            )
        # Identical response whether or not the email exists (no account enumeration).
        return ok(None, "If an account exists for that email, a reset link has been sent.")


@method_decorator(csrf_protect, name="dispatch")
class PasswordResetConfirmView(APIView):
    permission_classes = [AllowAny]
    throttle_classes = [ScopedRateThrottle]
    throttle_scope = "password_reset"

    def post(self, request):
        serializer = PasswordResetConfirmSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        data = serializer.validated_data
        try:
            user = User.objects.get(pk=force_str(urlsafe_base64_decode(data["uid"])), is_active=True)
        except (User.DoesNotExist, ValueError, TypeError, OverflowError):
            user = None
        if user is None or not default_token_generator.check_token(user, data["token"]):
            raise BusinessError("The reset link is invalid or has expired.", code="INVALID_TOKEN")
        password_validation.validate_password(data["new_password"], user)
        user.set_password(data["new_password"])
        user.save(update_fields=["password"])
        record(AuditAction.PASSWORD_RESET, user, user=user)
        return ok(None, "Password has been reset. You can now log in.")


class UserViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, mixins.CreateModelMixin,
                  mixins.UpdateModelMixin, viewsets.GenericViewSet):
    """User administration (ADMIN only). Users are deactivated, never deleted."""

    queryset = User.objects.all().order_by("username")
    serializer_class = AdminUserSerializer
    permission_classes = [RolePermission]
    permission_map = {"read": "users.manage", "write": "users.manage"}
    search_fields = ["username", "email", "first_name", "last_name"]
    filterset_fields = ["role", "is_active"]
    ordering_fields = ["username", "role", "date_joined", "last_login"]

    @transaction.atomic
    def perform_create(self, serializer):
        user = serializer.save()
        record(AuditAction.CREATE, user, after=snapshot(user))

    def create(self, request, *args, **kwargs):
        response = super().create(request, *args, **kwargs)
        return created(response.data, "User created successfully.")

    @transaction.atomic
    def perform_update(self, serializer):
        instance = serializer.instance
        before = snapshot(instance)
        old_role, old_active = instance.role, instance.is_active
        if instance == self.request.user and (
            serializer.validated_data.get("role", old_role) != Role.ADMIN
            or serializer.validated_data.get("is_active", True) is False
        ):
            raise BusinessError("You cannot remove your own administrator access.", code="SELF_LOCKOUT")
        user = serializer.save()
        after = snapshot(user)
        record(AuditAction.UPDATE, user, before=before, after=after)
        if user.role != old_role:
            record(AuditAction.PERMISSION_CHANGE, user, before={"role": old_role}, after={"role": user.role})
        if user.is_active != old_active:
            record(AuditAction.ACTIVATE if user.is_active else AuditAction.DEACTIVATE, user)

    def update(self, request, *args, **kwargs):
        response = super().update(request, *args, **kwargs)
        return ok(response.data, "User updated successfully.")

    @action(detail=False, methods=["get"])
    def roles(self, request):
        return ok(_roles_payload())


class RolesView(APIView):
    permission_classes = [IsAuthenticated]

    def get(self, request):
        return ok(_roles_payload())


def _roles_payload():
    return [
        {
            "role": role.value,
            "label": role.label,
            "description": ROLE_DESCRIPTIONS[role],
            "permissions": sorted(p for p, roles in PERMISSION_MATRIX.items() if role in roles),
        }
        for role in Role
    ]
