"""Bikram Sambat (B.S.) <-> Gregorian (A.D.) conversion for printed and exported dates.

Same month table as frontend/src/lib/utilities/nepali-date.ts (B.S. 2000-2090,
from the MIT-licensed nepali-date-converter package). Each year is 12 digits;
digit + 28 = days in that month. B.S. 2000-01-01 is A.D. 1943-04-14.
"""

import datetime as dt

_TABLE = (
    '243432221213,334333212122,334432212122,343432221123,243432221213,334333212122,334432212122,343432221123,333433122113,334333212122,334432212122,343432221123,333433122122,334333212122,334432212122,343432221123,333433122122,334333212122,343432212122,343432221213,333433212122,334333212122,343432221122,343432221213,333433212122,334333212122,343432221123,243432221213,334333212122,334342212122,343432221123,243432221213,334333212122,334432212122,343432221123,243433122113,334333212122,334432212122,343432221123,333433122122,334333212122,334432212122,343432221123,333433122122,334333212122,343432212122,343432221123,333433212122,334333212122,343432221122,343432221213,333433212122,334333212122,343432221122,343432221213,334333212122,334342212122,343432221123,243432221213,334333212122,334432212122,343432221123,243433121213,334333212122,334432212122,343432221123,333433122113,334333212122,334432212122,343432221123,333433122122,334333212122,343432212122,343432221123,333433212122,334333212122,343432221122,343432221213,333433212122,334333212122,343432221122,343432221213,334333212122,334333212122,343432221123,243432221213,334333212122,334333221222,234423221222,243432221222,243432221222'
)
BS_MIN_YEAR = 2000
_MONTH_DAYS = [[int(c) + 28 for c in row] for row in _TABLE.split(",")]
BS_MAX_YEAR = BS_MIN_YEAR + len(_MONTH_DAYS) - 1
_EPOCH = dt.date(1943, 4, 14)


def ad_to_bs(value: dt.date) -> tuple[int, int, int]:
    """A.D. date -> (year, month, day) in B.S."""
    days = (value - _EPOCH).days
    if days < 0:
        raise ValueError(f"{value} is before B.S. {BS_MIN_YEAR}")
    for i, months in enumerate(_MONTH_DAYS):
        for m, length in enumerate(months):
            if days < length:
                return BS_MIN_YEAR + i, m + 1, days + 1
            days -= length
    raise ValueError(f"{value} is after B.S. {BS_MAX_YEAR}")


def bs_to_ad(year: int, month: int, day: int) -> dt.date:
    """B.S. (year, month, day) -> A.D. date."""
    if not BS_MIN_YEAR <= year <= BS_MAX_YEAR or not 1 <= month <= 12:
        raise ValueError(f"B.S. {year}-{month} is outside the supported range")
    if not 1 <= day <= _MONTH_DAYS[year - BS_MIN_YEAR][month - 1]:
        raise ValueError(f"Invalid B.S. date {year}-{month}-{day}")
    days = sum(sum(m) for m in _MONTH_DAYS[: year - BS_MIN_YEAR])
    days += sum(_MONTH_DAYS[year - BS_MIN_YEAR][: month - 1]) + day - 1
    return _EPOCH + dt.timedelta(days=days)


def uses_bs() -> bool:
    """True when the active company shows dates in B.S."""
    from apps.common.models import BusinessSettings

    return BusinessSettings.get_solo().date_system == "BS"


def format_date(value: dt.date, *, bs: bool, ad_format: str = "%d/%m/%Y") -> str:
    """Printable date: "2083/06/27" in B.S. mode, otherwise ad_format."""
    if bs:
        try:
            y, m, d = ad_to_bs(value)
            return f"{y}/{m:02d}/{d:02d}"
        except ValueError:
            pass
    return value.strftime(ad_format)
