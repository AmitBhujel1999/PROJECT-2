"""Decimal helpers. Financial values are NEVER floats in this system.

Precision policy
----------------
* Money: 2 decimal places, ROUND_HALF_UP (standard commercial rounding).
* Quantity: 3 decimal places (supports Kg / Ltr / Meter).
* Rates (tax / percentage discount): 2 decimal places.
Rounding is applied per line (gross, discount, tax), and document totals are
the sum of already-rounded line values, so printed lines always add up.
"""

from decimal import ROUND_HALF_UP, Decimal, InvalidOperation

ZERO = Decimal("0")
HUNDRED = Decimal("100")
MONEY_PLACES = Decimal("0.01")
QTY_PLACES = Decimal("0.001")
RATE_PLACES = Decimal("0.01")

MONEY_FIELD = {"max_digits": 16, "decimal_places": 2}
QTY_FIELD = {"max_digits": 14, "decimal_places": 3}
RATE_FIELD = {"max_digits": 7, "decimal_places": 2}


def to_decimal(value) -> Decimal:
    if value is None or value == "":
        return ZERO
    if isinstance(value, Decimal):
        return value
    if isinstance(value, float):
        # Floats are converted via str() to avoid binary artefacts.
        value = repr(value)
    try:
        return Decimal(str(value))
    except (InvalidOperation, ValueError) as exc:
        raise ValueError(f"Invalid decimal value: {value!r}") from exc


def money(value) -> Decimal:
    return to_decimal(value).quantize(MONEY_PLACES, rounding=ROUND_HALF_UP)


def qty(value) -> Decimal:
    return to_decimal(value).quantize(QTY_PLACES, rounding=ROUND_HALF_UP)


def rate(value) -> Decimal:
    return to_decimal(value).quantize(RATE_PLACES, rounding=ROUND_HALF_UP)
