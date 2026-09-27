"""Accounts payable services (vendor payments, allocation, ledger, aging)."""

from __future__ import annotations

import datetime as dt

from apps.common import party_accounts as engine
from apps.common.models import DocumentSequence
from apps.parties.models import PartyType
from apps.purchases.models import Purchase

from .models import VendorPayment, VendorPaymentAllocation

PAYABLE = engine.AccountConfig(
    key="payable",
    party_type=PartyType.VENDOR,
    document_model=Purchase,
    doc_number="bill_number",
    doc_party="vendor",
    doc_label="Bill",
    payment_model=VendorPayment,
    pay_number="payment_number",
    pay_party="vendor",
    pay_label="Payment",
    allocation_model=VendorPaymentAllocation,
    alloc_doc="purchase",
    alloc_pay="payment",
    doc_alloc_related="payment_allocations",
    sequence_key=DocumentSequence.PAYMENT,
    document_is_debit=False,
)


def create_vendor_payment(*, vendor_id: int, date: dt.date, amount, payment_method: str, reference_number="",
                          notes="", allocations=None, auto_allocate=False, user) -> VendorPayment:
    return engine.create_payment(
        PAYABLE, party_id=vendor_id, date=date, amount=amount, payment_method=payment_method,
        reference_number=reference_number, notes=notes, allocations=allocations, auto_allocate=auto_allocate, user=user,
    )


def allocate_vendor_payment(payment: VendorPayment, allocations: list[dict], *, user):
    return engine.allocate(PAYABLE, payment, allocations, user=user)


def auto_allocate_vendor_payment(payment: VendorPayment, *, user):
    return engine.auto_allocate_payment(PAYABLE, payment, user=user)


def unallocate_vendor_payment(allocation_id: int, *, reason: str, user):
    return engine.unallocate(PAYABLE, allocation_id, reason=reason, user=user)


def cancel_vendor_payment(payment: VendorPayment, *, reason: str, user):
    return engine.cancel_payment(PAYABLE, payment, reason=reason, user=user)


def pay_purchase_on_creation(purchase: Purchase, *, payment: dict, user):
    return engine.pay_on_creation(PAYABLE, purchase, payment=payment, user=user)


def release_purchase_allocations(purchase: Purchase, *, user):
    return engine.release_document_allocations(PAYABLE, purchase, user=user)


def calculate_vendor_ledger(vendor_id: int, *, start=None, end=None) -> dict:
    return engine.ledger(PAYABLE, vendor_id, start=start, end=end)


def calculate_vendor_aging(as_of: dt.date, *, vendor_id=None, search=None) -> dict:
    return engine.aging_summary(PAYABLE, as_of, party_id=vendor_id, search=search)


def vendor_aging_detail(vendor_id: int, as_of: dt.date) -> dict:
    return engine.party_aging_detail(PAYABLE, vendor_id, as_of)


def vendor_balance(vendor_id: int, as_of: dt.date):
    return engine.balance_as_of(PAYABLE, as_of, vendor_id)
