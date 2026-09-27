"""Accounts receivable services (customer receipts, allocation, ledger, aging)."""

from __future__ import annotations

import datetime as dt

from apps.common import party_accounts as engine
from apps.common.models import DocumentSequence
from apps.parties.models import PartyType
from apps.sales.models import Sale

from .models import CustomerReceipt, CustomerReceiptAllocation

RECEIVABLE = engine.AccountConfig(
    key="receivable",
    party_type=PartyType.CUSTOMER,
    document_model=Sale,
    doc_number="invoice_number",
    doc_party="customer",
    doc_label="Invoice",
    payment_model=CustomerReceipt,
    pay_number="receipt_number",
    pay_party="customer",
    pay_label="Receipt",
    allocation_model=CustomerReceiptAllocation,
    alloc_doc="sale",
    alloc_pay="receipt",
    doc_alloc_related="receipt_allocations",
    sequence_key=DocumentSequence.RECEIPT,
    document_is_debit=True,
)


def create_customer_receipt(*, customer_id: int, date: dt.date, amount, payment_method: str, reference_number="",
                            notes="", allocations=None, auto_allocate=False, user) -> CustomerReceipt:
    return engine.create_payment(
        RECEIVABLE, party_id=customer_id, date=date, amount=amount, payment_method=payment_method,
        reference_number=reference_number, notes=notes, allocations=allocations, auto_allocate=auto_allocate, user=user,
    )


def allocate_customer_payment(receipt: CustomerReceipt, allocations: list[dict], *, user):
    return engine.allocate(RECEIVABLE, receipt, allocations, user=user)


def auto_allocate_customer_payment(receipt: CustomerReceipt, *, user):
    return engine.auto_allocate_payment(RECEIVABLE, receipt, user=user)


def unallocate_customer_payment(allocation_id: int, *, reason: str, user):
    return engine.unallocate(RECEIVABLE, allocation_id, reason=reason, user=user)


def cancel_customer_receipt(receipt: CustomerReceipt, *, reason: str, user):
    return engine.cancel_payment(RECEIVABLE, receipt, reason=reason, user=user)


def receive_on_sale_creation(sale: Sale, *, payment: dict, user):
    return engine.pay_on_creation(RECEIVABLE, sale, payment=payment, user=user)


def release_sale_allocations(sale: Sale, *, user):
    return engine.release_document_allocations(RECEIVABLE, sale, user=user)


def calculate_customer_ledger(customer_id: int, *, start=None, end=None) -> dict:
    return engine.ledger(RECEIVABLE, customer_id, start=start, end=end)


def calculate_customer_aging(as_of: dt.date, *, customer_id=None, search=None) -> dict:
    return engine.aging_summary(RECEIVABLE, as_of, party_id=customer_id, search=search)


def customer_aging_detail(customer_id: int, as_of: dt.date) -> dict:
    return engine.party_aging_detail(RECEIVABLE, customer_id, as_of)


def customer_balance(customer_id: int, as_of: dt.date):
    return engine.balance_as_of(RECEIVABLE, as_of, customer_id)
