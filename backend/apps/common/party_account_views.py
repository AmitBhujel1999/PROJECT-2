"""API views shared by receivables and payables (parameterised by AccountConfig)."""

from __future__ import annotations

from decimal import Decimal

from django.db.models import F, Q
from django.shortcuts import get_object_or_404
from django.utils import timezone
from rest_framework import mixins, serializers, viewsets
from rest_framework.decorators import action
from rest_framework.views import APIView

from apps.parties.models import Party
from apps.users.permissions import RolePermission

from . import party_accounts as engine
from .models import DocumentStatus, PaymentMethod
from .pagination import paginate_list
from .responses import created, ok
from .utils import parse_date_param


# ---------------------------------------------------------------------------
# Serializers
# ---------------------------------------------------------------------------
class AllocationInputSerializer(serializers.Serializer):
    document = serializers.IntegerField(min_value=1)
    amount = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0.01"))


class PaymentInputSerializer(serializers.Serializer):
    party = serializers.IntegerField(min_value=1)
    date = serializers.DateField()
    amount = serializers.DecimalField(max_digits=16, decimal_places=2, min_value=Decimal("0.01"))
    payment_method = serializers.ChoiceField(choices=PaymentMethod.choices, default=PaymentMethod.CASH)
    reference_number = serializers.CharField(max_length=60, required=False, allow_blank=True, default="")
    notes = serializers.CharField(max_length=2000, required=False, allow_blank=True, default="")
    allocations = AllocationInputSerializer(many=True, required=False)
    auto_allocate = serializers.BooleanField(required=False, default=False)


class AllocateSerializer(serializers.Serializer):
    allocations = AllocationInputSerializer(many=True)


class ReasonSerializer(serializers.Serializer):
    reason = serializers.CharField(max_length=255)


class UnallocateSerializer(serializers.Serializer):
    allocation = serializers.IntegerField(min_value=1)
    reason = serializers.CharField(max_length=255, required=False, allow_blank=True, default="")


def payment_payload(cfg: engine.AccountConfig, payment, *, detail=False) -> dict:
    party = getattr(payment, cfg.pay_party)
    data = {
        "id": payment.pk,
        "number": cfg.pay_no(payment),
        cfg.pay_number: cfg.pay_no(payment),
        "party": party.pk,
        "party_name": party.name,
        "date": payment.date,
        "amount": payment.amount,
        "allocated_amount": payment.allocated_amount,
        "unallocated_amount": payment.unallocated_amount,
        "payment_method": payment.payment_method,
        "payment_method_display": payment.get_payment_method_display(),
        "reference_number": payment.reference_number,
        "notes": payment.notes,
        "status": payment.status,
        "created_at": payment.created_at,
        "created_by_name": payment.created_by.username if payment.created_by_id else None,
    }
    if detail:
        data.update(
            {
                "cancelled_at": payment.cancelled_at,
                "cancel_reason": payment.cancel_reason,
                "party_pan_vat_no": party.pan_vat_no,
                "party_address": party.address,
                "allocations": [
                    {
                        "id": a.pk,
                        "document_id": getattr(a, f"{cfg.alloc_doc}_id"),
                        "document_number": cfg.doc_no(getattr(a, cfg.alloc_doc)),
                        "document_date": getattr(a, cfg.alloc_doc).date,
                        "document_total": getattr(a, cfg.alloc_doc).total_amount,
                        "amount": a.amount,
                        "date": a.date,
                        "is_active": a.is_active,
                        "voided_at": a.voided_at,
                        "void_reason": a.void_reason,
                    }
                    for a in payment.allocations.select_related(cfg.alloc_doc).order_by("id")
                ],
            }
        )
    return data


# ---------------------------------------------------------------------------
# Payment (receipt / vendor payment) viewset
# ---------------------------------------------------------------------------
class BasePaymentViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, mixins.CreateModelMixin, viewsets.GenericViewSet):
    cfg: engine.AccountConfig
    view_perm: str
    create_perm: str
    cancel_perm: str
    success_label: str
    ordering_fields = ["date", "amount", "id"]

    permission_classes = [RolePermission]

    @property
    def permission_map(self):
        return {
            "read": self.view_perm,
            "open_documents": self.view_perm,
            "create": self.create_perm,
            "allocate": self.create_perm,
            "auto_allocate": self.create_perm,
            "unallocate": self.create_perm,
            "cancel": self.cancel_perm,
        }

    def get_queryset(self):
        cfg = self.cfg
        qs = cfg.payment_model.objects.select_related(cfg.pay_party, "created_by").order_by("-date", "-id")
        params = self.request.query_params
        if params.get("party"):
            qs = qs.filter(**{f"{cfg.pay_party}_id": params["party"]})
        if params.get("status"):
            qs = qs.filter(status=params["status"])
        if params.get("payment_method"):
            qs = qs.filter(payment_method=params["payment_method"])
        if params.get("unallocated") == "true":
            qs = qs.filter(status=DocumentStatus.ACTIVE, allocated_amount__lt=F("amount"))
        start = parse_date_param(self.request, "start_date")
        end = parse_date_param(self.request, "end_date")
        if start:
            qs = qs.filter(date__gte=start)
        if end:
            qs = qs.filter(date__lte=end)
        search = params.get("search")
        if search:
            qs = qs.filter(
                Q(**{f"{cfg.pay_number}__icontains": search})
                | Q(**{f"{cfg.pay_party}__name__icontains": search})
                | Q(reference_number__icontains=search)
            )
        return qs

    def list(self, request, *args, **kwargs):
        page = self.paginate_queryset(self.get_queryset())
        return self.get_paginated_response([payment_payload(self.cfg, p) for p in page])

    def retrieve(self, request, *args, **kwargs):
        payment = get_object_or_404(self.cfg.payment_model.objects.select_related(self.cfg.pay_party), pk=kwargs["pk"])
        return ok(payment_payload(self.cfg, payment, detail=True))

    def create(self, request, *args, **kwargs):
        s = PaymentInputSerializer(data=request.data)
        s.is_valid(raise_exception=True)
        d = s.validated_data
        payment = engine.create_payment(
            self.cfg,
            party_id=d["party"],
            date=d["date"],
            amount=d["amount"],
            payment_method=d["payment_method"],
            reference_number=d.get("reference_number", ""),
            notes=d.get("notes", ""),
            allocations=d.get("allocations") or None,
            auto_allocate=d.get("auto_allocate", False),
            user=request.user,
        )
        return created(payment_payload(self.cfg, payment, detail=True), f"{self.success_label} {self.cfg.pay_no(payment)} recorded successfully.")

    @action(detail=True, methods=["post"])
    def allocate(self, request, pk=None):
        s = AllocateSerializer(data=request.data)
        s.is_valid(raise_exception=True)
        payment = get_object_or_404(self.cfg.payment_model, pk=pk)
        engine.allocate(self.cfg, payment, s.validated_data["allocations"], user=request.user)
        payment.refresh_from_db()
        return ok(payment_payload(self.cfg, payment, detail=True), f"{self.cfg.pay_no(payment)} allocated.")

    @action(detail=True, methods=["post"], url_path="auto-allocate")
    def auto_allocate(self, request, pk=None):
        payment = get_object_or_404(self.cfg.payment_model, pk=pk)
        allocs = engine.auto_allocate_payment(self.cfg, payment, user=request.user)
        payment.refresh_from_db()
        msg = f"Allocated to {len(allocs)} document(s)." if allocs else "No open documents to allocate to."
        return ok(payment_payload(self.cfg, payment, detail=True), msg)

    @action(detail=True, methods=["post"])
    def unallocate(self, request, pk=None):
        s = UnallocateSerializer(data=request.data)
        s.is_valid(raise_exception=True)
        payment = get_object_or_404(self.cfg.payment_model, pk=pk)
        alloc_ok = payment.allocations.filter(pk=s.validated_data["allocation"]).exists()
        if not alloc_ok:
            from .exceptions import AllocationError

            raise AllocationError("Allocation does not belong to this payment.")
        engine.unallocate(self.cfg, s.validated_data["allocation"], reason=s.validated_data["reason"], user=request.user)
        payment.refresh_from_db()
        return ok(payment_payload(self.cfg, payment, detail=True), "Allocation removed.")

    @action(detail=True, methods=["post"])
    def cancel(self, request, pk=None):
        s = ReasonSerializer(data=request.data)
        s.is_valid(raise_exception=True)
        payment = get_object_or_404(self.cfg.payment_model, pk=pk)
        payment = engine.cancel_payment(self.cfg, payment, reason=s.validated_data["reason"], user=request.user)
        return ok(payment_payload(self.cfg, payment, detail=True), f"{self.cfg.pay_no(payment)} cancelled.")

    @action(detail=False, methods=["get"], url_path="open-documents")
    def open_documents(self, request):
        """Open invoices/bills of a party, oldest due first (for allocation UI)."""
        party_id = request.query_params.get("party")
        if not party_id or not party_id.isdigit():
            raise serializers.ValidationError({"party": "This parameter is required."})
        today = timezone.localdate()
        rows = [
            {
                "id": d.pk,
                "number": self.cfg.doc_no(d),
                "date": d.date,
                "due_date": d.due_date,
                "total_amount": d.total_amount,
                "amount_paid": d.amount_paid,
                "balance_due": d.balance_due,
                "payment_status": d.payment_status,
                "days_overdue": max((today - d.due_date).days, 0),
            }
            for d in engine.open_documents(self.cfg, int(party_id))[:500]
        ]
        return ok(rows)


# ---------------------------------------------------------------------------
# Party ledger / statement / aging / summary
# ---------------------------------------------------------------------------
class PartyAccountView(APIView):
    cfg: engine.AccountConfig
    permission_classes = [RolePermission]
    permission_map = {"read": "ledgers.view"}

    def party(self, pk):
        return get_object_or_404(Party, pk=pk, type=self.cfg.party_type)


def party_header(party) -> dict:
    return {
        "id": party.pk,
        "name": party.name,
        "type": party.type,
        "pan_vat_no": party.pan_vat_no,
        "phone": party.phone,
        "email": party.email,
        "address": party.address,
        "credit_terms": party.get_credit_terms_display(),
        "credit_days": party.credit_days,
        "credit_limit": party.credit_limit,
    }


class PartyLedgerView(PartyAccountView):
    def get(self, request, pk):
        party = self.party(pk)
        data = engine.ledger(
            self.cfg, party.pk, start=parse_date_param(request, "start_date"), end=parse_date_param(request, "end_date")
        )
        entries = data.pop("entries")
        return paginate_list(request, entries, view=self, extra={"party": party_header(party), **data})


class PartyStatementView(PartyAccountView):
    def get(self, request, pk):
        party = self.party(pk)
        today = timezone.localdate()
        end = parse_date_param(request, "end_date", today)
        start = parse_date_param(request, "start_date", end.replace(day=1))
        data = engine.ledger(self.cfg, party.pk, start=start, end=end)
        return ok({"party": party_header(party), **data})


class PartyAgingView(PartyAccountView):
    def get(self, request, pk):
        party = self.party(pk)
        as_of = parse_date_param(request, "as_of", timezone.localdate())
        return ok({"party": party_header(party), **engine.party_aging_detail(self.cfg, party.pk, as_of)})


class PartySummaryView(APIView):
    cfg: engine.AccountConfig
    permission_classes = [RolePermission]
    permission_map = {"read": "parties.view"}

    def get(self, request, pk):
        party = get_object_or_404(Party, pk=pk, type=self.cfg.party_type)
        return ok({"party": party_header(party), **engine.party_summary(self.cfg, party)})
