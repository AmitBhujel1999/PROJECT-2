from django.db import transaction

from apps.audit.models import AuditAction
from apps.audit.services import record, snapshot

from .models import Party


@transaction.atomic
def create_party(*, data: dict, user) -> Party:
    party = Party.objects.create(**data)
    record(AuditAction.CREATE, party, after=snapshot(party), user=user)
    return party


@transaction.atomic
def update_party(party: Party, *, data: dict, user) -> Party:
    party = Party.objects.select_for_update().get(pk=party.pk)
    before = snapshot(party)
    data.pop("type", None)  # a party's type never changes (ledgers depend on it)
    for key, value in data.items():
        setattr(party, key, value)
    party.save()
    record(AuditAction.UPDATE, party, before=before, after=snapshot(party), user=user)
    return party


@transaction.atomic
def set_party_active(party: Party, *, active: bool, user) -> Party:
    before = snapshot(party)
    party.is_active = active
    party.save(update_fields=["is_active", "updated_at"])
    record(AuditAction.ACTIVATE if active else AuditAction.DEACTIVATE, party, before=before, after=snapshot(party), user=user)
    return party
