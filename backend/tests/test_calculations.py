from decimal import Decimal as D

import pytest

from apps.common.calculations import CalculationError, LineInput, calculate_document, compute_discount


def doc(lines, dtype=None, dval=0):
    return calculate_document([LineInput(*ln) for ln in lines], dtype, D(str(dval)))


def test_spec_purchase_example_percentage_item_discount():
    # Qty 10 x 1,000, 10% discount, 13% VAT -> 10,170
    t = doc([(D(10), D(1000), D(13), "PERCENTAGE", D(10))])
    ln = t.lines[0]
    assert ln.gross_amount == D("10000.00")
    assert ln.discount_amount == D("1000.00")
    assert ln.taxable_amount == D("9000.00")
    assert ln.tax_amount == D("1170.00")
    assert ln.total == D("10170.00")
    assert t.total_amount == D("10170.00")


def test_spec_sale_example():
    # Qty 5 x 2,000, 10% -> taxable 9,000, VAT 1,170, total 10,170
    t = doc([(D(5), D(2000), D(13), "PERCENTAGE", D(10))])
    assert (t.taxable_amount, t.tax_amount, t.total_amount) == (D("9000.00"), D("1170.00"), D("10170.00"))


def test_fixed_item_discount():
    t = doc([(D(2), D(750), D(13), "FIXED", D(100))])
    assert t.lines[0].discount_amount == D("100.00")
    assert t.taxable_amount == D("1400.00")
    assert t.tax_amount == D("182.00")
    assert t.total_amount == D("1582.00")


def test_invoice_percentage_discount_applied_after_item_discount():
    t = doc([(D(5), D(500), D(13), "PERCENTAGE", D(10)), (D(2), D(750), D(13))], "PERCENTAGE", 10)
    assert t.subtotal == D("4000.00")
    assert t.item_discount_total == D("250.00")
    assert t.net_subtotal == D("3750.00")
    assert t.discount_amount == D("375.00")
    assert t.total_discount == D("625.00")
    assert t.taxable_amount == D("3375.00")
    assert t.tax_amount == D("438.75")
    assert t.total_amount == D("3813.75")


def test_invoice_fixed_discount_allocated_pro_rata_and_sums_exactly():
    t = doc([(D(1), D("100"), D(13)), (D(1), D("100"), D(13)), (D(1), D("100"), D(13))], "FIXED", 100)
    shares = [ln.invoice_discount_share for ln in t.lines]
    assert sum(shares) == D("100.00")
    assert t.taxable_amount == D("200.00")
    assert sum(ln.taxable_amount for ln in t.lines) == t.taxable_amount


def test_mixed_tax_rates_computed_per_line():
    t = doc([(D(1), D(1000), D(13)), (D(1), D(1000), D(0))], "FIXED", 200)
    assert t.lines[0].taxable_amount == D("900.00")
    assert t.lines[0].tax_amount == D("117.00")
    assert t.lines[1].tax_amount == D("0.00")
    assert t.total_amount == D("1917.00")


def test_rounding_half_up():
    t = doc([(D("3"), D("33.33"), D("13"))])
    # 99.99 * 13% = 12.9987 -> 13.00
    assert t.tax_amount == D("13.00")
    assert t.total_amount == D("112.99")


def test_fractional_quantities():
    t = doc([(D("2.5"), D("140"), D("0"))])
    assert t.subtotal == D("350.00")


@pytest.mark.parametrize(
    "lines,dtype,dval,code",
    [
        ([(D(1), D(100), D(13), "FIXED", D(150))], None, 0, "INVALID_DISCOUNT"),
        ([(D(1), D(100), D(13), "PERCENTAGE", D(101))], None, 0, "INVALID_DISCOUNT"),
        ([(D(1), D(100), D(13), "PERCENTAGE", D(-1))], None, 0, "INVALID_DISCOUNT"),
        ([(D(1), D(100), D(13))], "FIXED", 101, "INVALID_DISCOUNT"),
        ([(D(0), D(100), D(13))], None, 0, "INVALID_QUANTITY"),
        ([(D(1), D(-1), D(13))], None, 0, "INVALID_PRICE"),
        ([(D(1), D(1), D(101))], None, 0, "INVALID_TAX_RATE"),
    ],
)
def test_invalid_inputs_rejected(lines, dtype, dval, code):
    with pytest.raises(CalculationError) as exc:
        doc(lines, dtype, dval)
    assert exc.value.code == code


def test_no_items_rejected():
    with pytest.raises(CalculationError):
        calculate_document([])


def test_full_discount_gives_zero_not_negative():
    t = doc([(D(1), D(100), D(13), "PERCENTAGE", D(100))])
    assert t.taxable_amount == D("0.00")
    assert t.total_amount == D("0.00")


def test_compute_discount_none():
    assert compute_discount(D(100), None, 50) == D("0.00")
