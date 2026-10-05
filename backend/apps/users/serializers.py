from django.contrib.auth import password_validation
from rest_framework import serializers

from .models import Role, User
from .permissions import permissions_for


class UserSerializer(serializers.ModelSerializer):
    display_name = serializers.CharField(read_only=True)
    permissions = serializers.SerializerMethodField()

    class Meta:
        model = User
        fields = [
            "id", "username", "email", "first_name", "last_name", "phone", "role",
            "is_active", "last_login", "date_joined", "display_name", "permissions",
        ]
        read_only_fields = ["id", "username", "role", "is_active", "last_login", "date_joined"]

    def get_permissions(self, obj):
        return permissions_for(obj)


class LoginSerializer(serializers.Serializer):
    username = serializers.CharField(max_length=150)
    password = serializers.CharField(max_length=128, trim_whitespace=False)
    company = serializers.CharField(max_length=50, required=False)


class PasswordChangeSerializer(serializers.Serializer):
    current_password = serializers.CharField(trim_whitespace=False)
    new_password = serializers.CharField(trim_whitespace=False)

    def validate(self, attrs):
        user = self.context["request"].user
        if not user.check_password(attrs["current_password"]):
            raise serializers.ValidationError({"current_password": "Current password is incorrect."})
        password_validation.validate_password(attrs["new_password"], user)
        return attrs


class PasswordResetRequestSerializer(serializers.Serializer):
    email = serializers.EmailField()


class PasswordResetConfirmSerializer(serializers.Serializer):
    uid = serializers.CharField()
    token = serializers.CharField()
    new_password = serializers.CharField(trim_whitespace=False)


class AdminUserSerializer(serializers.ModelSerializer):
    """Used by administrators to manage accounts and roles."""

    password = serializers.CharField(write_only=True, required=False, trim_whitespace=False)
    display_name = serializers.CharField(read_only=True)

    class Meta:
        model = User
        fields = [
            "id", "username", "email", "first_name", "last_name", "phone", "role",
            "is_active", "last_login", "date_joined", "display_name", "password",
        ]
        read_only_fields = ["id", "last_login", "date_joined"]

    def validate_role(self, value):
        if value not in Role.values:
            raise serializers.ValidationError("Invalid role.")
        return value

    def validate(self, attrs):
        password = attrs.get("password")
        if self.instance is None and not password:
            raise serializers.ValidationError({"password": "Password is required for new users."})
        if password:
            candidate = self.instance or User(username=attrs.get("username", ""), email=attrs.get("email", ""))
            password_validation.validate_password(password, candidate)
        return attrs

    def create(self, validated_data):
        password = validated_data.pop("password")
        user = User(**validated_data)
        user.set_password(password)
        user.save()
        return user

    def update(self, instance, validated_data):
        password = validated_data.pop("password", None)
        for key, value in validated_data.items():
            setattr(instance, key, value)
        if password:
            instance.set_password(password)
        instance.save()
        return instance
