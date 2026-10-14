import django_filters
from rest_framework import mixins, viewsets
from rest_framework.decorators import action

from apps.common.bulk import BulkConfig, bulk_save, summary_message
from apps.common.responses import created, ok
from apps.users.permissions import RolePermission

from . import services
from .models import Party, PartyType
from .serializers import PartySerializer


class PartyFilter(django_filters.FilterSet):
    class Meta:
        model = Party
        fields = ["type", "is_active", "credit_terms"]


class BasePartyViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, mixins.CreateModelMixin,
                       mixins.UpdateModelMixin, viewsets.GenericViewSet):
    """Parties are never deleted — they are deactivated."""

    serializer_class = PartySerializer
    permission_classes = [RolePermission]
    permission_map = {
        "read": "parties.view",
        "create": "parties.create",
        "update": "parties.manage",
        "partial_update": "parties.manage",
        "activate": "parties.manage",
        "deactivate": "parties.manage",
        "bulk": "parties.manage",
    }
    filterset_class = PartyFilter
    search_fields = ["name", "phone", "pan_vat_no", "email"]
    ordering_fields = ["name", "created_at", "credit_limit"]
    ordering = ["name"]
    party_type: str | None = None

    def get_queryset(self):
        qs = Party.objects.all()
        if self.party_type:
            qs = qs.filter(type=self.party_type)
        return qs

    def _label(self, party):
        return party.get_type_display()

    def create(self, request, *args, **kwargs):
        data = {k: (v[0] if isinstance(v, list) and len(v) == 1 else v) for k, v in request.data.items()}
        if self.party_type:
            data["type"] = self.party_type
        serializer = self.get_serializer(data=data)
        serializer.is_valid(raise_exception=True)
        party = services.create_party(data=serializer.validated_data, user=request.user)
        return created(self.get_serializer(party).data, f"{self._label(party)} {party.name} created successfully.")

    def update(self, request, *args, **kwargs):
        partial = kwargs.pop("partial", False)
        instance = self.get_object()
        serializer = self.get_serializer(instance, data=request.data, partial=partial)
        serializer.is_valid(raise_exception=True)
        party = services.update_party(instance, data=dict(serializer.validated_data), user=request.user)
        return ok(self.get_serializer(party).data, f"{self._label(party)} {party.name} updated successfully.")

    @action(detail=True, methods=["post"])
    def deactivate(self, request, pk=None):
        party = services.set_party_active(self.get_object(), active=False, user=request.user)
        return ok(self.get_serializer(party).data, f"{party.name} deactivated.")

    @action(detail=True, methods=["post"])
    def activate(self, request, pk=None):
        party = services.set_party_active(self.get_object(), active=True, user=request.user)
        return ok(self.get_serializer(party).data, f"{party.name} activated.")

    @action(detail=False, methods=["post"])
    def bulk(self, request):
        """Create/update many parties from the bulk-edit grid: {"rows": [{id?, field: value, ...}]}."""
        if not self.party_type:
            return ok(None, "Use the customers or vendors endpoint.", status=400)
        cfg = BulkConfig(
            serializer_class=PartySerializer,
            queryset=self.get_queryset,
            fields=["name", "pan_vat_no", "phone", "email", "address", "credit_terms", "credit_days", "credit_limit", "notes"],
            create=lambda data, user: services.create_party(data=data, user=user),
            update=lambda obj, data, user: services.update_party(obj, data=data, user=user),
            set_active=lambda obj, active, user: services.set_party_active(obj, active=active, user=user),
            defaults={"type": self.party_type},
        )
        result = bulk_save(cfg, request.data.get("rows"), user=request.user, context=self.get_serializer_context())
        return ok(result, summary_message(result, PartyType(self.party_type).label.lower()))


class PartyViewSet(BasePartyViewSet):
    pass


class CustomerViewSet(BasePartyViewSet):
    party_type = PartyType.CUSTOMER


class VendorViewSet(BasePartyViewSet):
    party_type = PartyType.VENDOR
