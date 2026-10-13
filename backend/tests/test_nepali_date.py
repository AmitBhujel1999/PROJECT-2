import datetime as dt

import pytest

from apps.common.nepali_date import BS_MAX_YEAR, BS_MIN_YEAR, ad_to_bs, bs_to_ad, format_date
from apps.expenses.services import ensure_default_categories


@pytest.mark.parametrize(
    "ad, bs",
    [
        (dt.date(1943, 4, 14), (2000, 1, 1)),
        (dt.date(2026, 10, 13), (2083, 6, 27)),
        (dt.date(2026, 7, 17), (2083, 4, 1)),  # Shrawan 1: fiscal year start
        (dt.date(2024, 4, 13), (2081, 1, 1)),  # Nepali new year 2081
    ],
)
def test_known_dates(ad, bs):
    assert ad_to_bs(ad) == bs
    assert bs_to_ad(*bs) == ad


def test_round_trip_whole_range():
    day = dt.date(1943, 4, 14)
    while True:
        try:
            bs = ad_to_bs(day)
        except ValueError:
            break
        assert bs_to_ad(*bs) == day
        day += dt.timedelta(days=1)
    assert ad_to_bs(day - dt.timedelta(days=1))[0] == BS_MAX_YEAR


@pytest.mark.parametrize("bad", [(BS_MIN_YEAR - 1, 1, 1), (2083, 13, 1), (2083, 6, 0), (2083, 6, 32)])
def test_invalid_bs_dates(bad):
    with pytest.raises(ValueError):
        bs_to_ad(*bad)


def test_format_date():
    assert format_date(dt.date(2026, 10, 13), bs=True) == "2083/06/27"
    assert format_date(dt.date(2026, 10, 13), bs=False) == "13/10/2026"
    # Outside the table falls back to A.D.
    assert format_date(dt.date(1900, 1, 1), bs=True) == "01/01/1900"


def test_date_system_setting(api):
    assert api.get("/api/settings/").json()["data"]["date_system"] == ""
    r = api.put("/api/settings/", {"date_system": "BS"}, format="json")
    assert r.status_code == 200 and r.json()["data"]["date_system"] == "BS"
    assert api.put("/api/settings/", {"date_system": "XX"}, format="json").status_code == 400


def test_exports_use_bs_dates(api, db):
    rent = ensure_default_categories()["Rent"]
    date = dt.date(2026, 10, 13)
    payload = {"date": date.isoformat(), "category": rent.pk, "description": "Ashwin rent", "amount": "1000"}
    expense_id = api.post("/api/expenses/", payload, format="json").json()["data"]["id"]
    params = {"export": "csv", "start_date": date.isoformat(), "end_date": date.isoformat()}

    csv = api.get("/api/reports/expenses/", params).content.decode("utf-8-sig")
    assert "2026-10-13" in csv and "2083/06/27" not in csv

    api.put("/api/settings/", {"date_system": "BS"}, format="json")
    csv = api.get("/api/reports/expenses/", params).content.decode("utf-8-sig")
    assert "2083/06/27" in csv and "2026-10-13" not in csv
    assert api.get(f"/api/expenses/{expense_id}/pdf/").content.startswith(b"%PDF")
