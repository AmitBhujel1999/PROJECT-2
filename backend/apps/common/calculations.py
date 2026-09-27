"""The single financial calculation engine for sales and purchases.

Calculation order (identical for sales invoices and purchase bills)::

    Quantity x Unit Price            -> Gross Amount        (per line, rounded)
    Gross Amount - Item Discount     -> Net Item Amount     (per line)
    Sum(Net Item Amounts)            -> Net Subtotal
    Invoice-level Discount           -> allocated to lines pro-rata by net amount
    Net Item Amount - Invoice share  -> Taxable Amount      (per line)
    Taxable Amount x Line Tax Rate   -> Tax Amount          (per line, rounded)
    Taxable Amount + Tax Amount      -> Line Total
    Sum(line values)                 -> Document totals

Why allocate the invoice discount to lines?  Products carry different tax
rates, so VAT must be computed on each line's own taxable amount. Allocating
pro-rata keeps the VAT exact and lets every printed line add up to the totals.
The allocation uses rounded shares with the rounding remainder assigned to
the largest line, so the shares always sum exactly to the invoice discount.

Validation:
* quantity > 0, unit price >= 0, tax rate 0..100
* discount value >= 0; percentage <= 100; fixed discount <= the amount it
  applies to. A discount can therefore never produce a negative taxable amount.

The frontend never supplies totals; any it sends are ignored.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from decimal import Decimal

from .exceptions import BusinessError
from .models import DiscountType
from .money import HUNDRED, ZERO, money, qty, rate, to_decimal


class CalculationError(BusinessError):
    default_code = "INVALID_AMOUNT"


@dataclass
class LineInput:
    quantity: Decimal
    unit_price: Decimal
    tax_rate: Decimal
    discount_type: str | None = None
    discount_value: Decimal = ZERO
    ref: object = None  # opaque payload (e.g. the product) carried through


@dataclass
class LineResult:
    ref: object
    quantity: Decimal
    unit_price: Decimal
    gross_amount: Decimal
    discount_type: str | None
    discount_value: Decimal
    discount_amount: Decimal
    net_amount: Decimal
    invoice_discount_share: Decimal
    taxable_amount: Decimal
    tax_rate: Decimal
    tax_amount: Decimal
    total: Decimal

    def as_dict(self) -> dict:
        return {k: v for k, v in self.__dict__.items() if k != "ref"}


@dataclass
class DocumentTotals:
    lines: list[LineResult] = field(default_factory=list)
    subtotal: Decimal = ZERO  # sum of gross amounts
    item_discount_total: Decimal = ZERO
    net_subtotal: Decimal = ZERO  # after item discounts
    discount_type: str | None = None
    discount_value: Decimal = ZERO
    discount_amount: Decimal = ZERO  # invoice-level discount
    total_discount: Decimal = ZERO
    taxable_amount: Decimal = ZERO
    tax_amount: Decimal = ZERO
    total_amount: Decimal = ZERO

    def as_dict(self) -> dict:
        data = {k: v for k, v in self.__dict__.items() if k != "lines"}
        data["lines"] = [line.as_dict() for line in self.lines]
        return data


def compute_discount(base: Decimal, discount_type: str | None, value, *, label: str = "Discount") -> Decimal:
    """Return the rounded discount amount on ``base`` and validate it."""
    value = to_decimal(value)
    if not discount_type or value == 0:
        return money(ZERO)
    if value < 0:
        raise CalculationError(f"{label} cannot be negative.", code="INVALID_DISCOUNT")
    if discount_type == DiscountType.PERCENTAGE:
        if value > HUNDRED:
            raise CalculationError(f"{label} percentage cannot exceed 100%.", code="INVALID_DISCOUNT")
        return money(base * value / HUNDRED)
    if discount_type == DiscountType.FIXED:
        amount = money(value)
        if amount > base:
            raise CalculationError(
                f"{label} ({amount}) cannot exceed the amount it applies to ({money(base)}).",
                code="INVALID_DISCOUNT",
            )
        return amount
    raise CalculationError(f"Unknown discount type: {discount_type}.", code="INVALID_DISCOUNT")


def calculate_line(line: LineInput, index: int = 0) -> LineResult:
    quantity = qty(line.quantity)
    unit_price = money(line.unit_price)
    tax_rate = rate(line.tax_rate)
    label = f"Line {index + 1}"
    if quantity <= 0:
        raise CalculationError(f"{label}: quantity must be greater than zero.", code="INVALID_QUANTITY")
    if unit_price < 0:
        raise CalculationError(f"{label}: price cannot be negative.", code="INVALID_PRICE")
    if tax_rate < 0 or tax_rate > HUNDRED:
        raise CalculationError(f"{label}: tax rate must be between 0 and 100.", code="INVALID_TAX_RATE")
    gross = money(quantity * unit_price)
    discount_amount = compute_discount(gross, line.discount_type, line.discount_value, label=f"{label} discount")
    dtype = line.discount_type if discount_amount > 0 or to_decimal(line.discount_value) > 0 else None
    return LineResult(
        ref=line.ref,
        quantity=quantity,
        unit_price=unit_price,
        gross_amount=gross,
        discount_type=dtype,
        discount_value=to_decimal(line.discount_value) if dtype else ZERO,
        discount_amount=discount_amount,
        net_amount=gross - discount_amount,
        invoice_discount_share=money(ZERO),
        taxable_amount=gross - discount_amount,
        tax_rate=tax_rate,
        tax_amount=money(ZERO),
        total=money(ZERO),
    )


def _allocate(amount: Decimal, lines: list[LineResult]) -> None:
    """Allocate an invoice discount across lines pro-rata by net amount."""
    net_total = sum((ln.net_amount for ln in lines), ZERO)
    if amount <= 0 or net_total <= 0:
        return
    allocated = ZERO
    for ln in lines:
        ln.invoice_discount_share = money(amount * ln.net_amount / net_total)
        allocated += ln.invoice_discount_share
    remainder = amount - allocated
    if remainder:
        largest = max(lines, key=lambda ln: ln.net_amount)
        largest.invoice_discount_share += remainder
    for ln in lines:
        if ln.invoice_discount_share > ln.net_amount:  # pragma: no cover - defensive
            raise CalculationError("Invoice discount allocation exceeded a line amount.", code="INVALID_DISCOUNT")


def calculate_document(lines: list[LineInput], discount_type: str | None = None, discount_value=ZERO) -> DocumentTotals:
    if not lines:
        raise CalculationError("At least one item is required.", code="NO_ITEMS")
    results = [calculate_line(line, i) for i, line in enumerate(lines)]
    subtotal = sum((r.gross_amount for r in results), ZERO)
    item_discounts = sum((r.discount_amount for r in results), ZERO)
    net_subtotal = subtotal - item_discounts
    invoice_discount = compute_discount(net_subtotal, discount_type, discount_value, label="Invoice discount")
    _allocate(invoice_discount, results)
    for r in results:
        r.taxable_amount = money(r.net_amount - r.invoice_discount_share)
        r.tax_amount = money(r.taxable_amount * r.tax_rate / HUNDRED)
        r.total = money(r.taxable_amount + r.tax_amount)
    taxable = sum((r.taxable_amount for r in results), ZERO)
    tax = sum((r.tax_amount for r in results), ZERO)
    has_invoice_discount = bool(discount_type) and to_decimal(discount_value) > 0
    return DocumentTotals(
        lines=results,
        subtotal=money(subtotal),
        item_discount_total=money(item_discounts),
        net_subtotal=money(net_subtotal),
        discount_type=discount_type if has_invoice_discount else None,
        discount_value=to_decimal(discount_value) if has_invoice_discount else ZERO,
        discount_amount=invoice_discount,
        total_discount=money(item_discounts + invoice_discount),
        taxable_amount=money(taxable),
        tax_amount=money(tax),
        total_amount=money(taxable + tax),
    )
