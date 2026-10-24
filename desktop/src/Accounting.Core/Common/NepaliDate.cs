namespace Accounting.Core.Common;

/// <summary>
/// Bikram Sambat (B.S.) &lt;-&gt; Gregorian (A.D.) conversion, ported from backend/apps/common/nepali_date.py
/// (same month table, B.S. 2000-2090). Each year is 12 digits; digit + 28 = days in that month.
/// B.S. 2000-01-01 is A.D. 1943-04-14.
/// </summary>
public static class NepaliDate
{
    private const string Table =
        "243432221213,334333212122,334432212122,343432221123,243432221213,334333212122,334432212122,343432221123,333433122113,334333212122,334432212122,343432221123,333433122122,334333212122,334432212122,343432221123,333433122122,334333212122,343432212122,343432221213,333433212122,334333212122,343432221122,343432221213,333433212122,334333212122,343432221123,243432221213,334333212122,334342212122,343432221123,243432221213,334333212122,334432212122,343432221123,243433122113,334333212122,334432212122,343432221123,333433122122,334333212122,334432212122,343432221123,333433122122,334333212122,343432212122,343432221123,333433212122,334333212122,343432221122,343432221213,333433212122,334333212122,343432221122,343432221213,334333212122,334342212122,343432221123,243432221213,334333212122,334432212122,343432221123,243433121213,334333212122,334432212122,343432221123,333433122113,334333212122,334432212122,343432221123,333433122122,334333212122,343432212122,343432221123,333433212122,334333212122,343432221122,343432221213,333433212122,334333212122,343432221122,343432221213,334333212122,334333212122,343432221123,243432221213,334333212122,334333221222,234423221222,243432221222,243432221222";

    public const int MinYear = 2000;
    private static readonly int[][] MonthDays = Table.Split(',').Select(row => row.Select(c => c - '0' + 28).ToArray()).ToArray();
    public static int MaxYear => MinYear + MonthDays.Length - 1;
    private static readonly DateOnly Epoch = new(1943, 4, 14);

    public static readonly IReadOnlyList<string> MonthNames =
        ["Baishakh", "Jestha", "Asar", "Shrawan", "Bhadra", "Ashwin", "Kartik", "Mangsir", "Poush", "Magh", "Falgun", "Chaitra"];

    /// <summary>A.D. date to (year, month, day) in B.S.</summary>
    public static (int Year, int Month, int Day) FromAd(DateOnly value)
    {
        var days = value.DayNumber - Epoch.DayNumber;
        if (days < 0)
            throw new ArgumentOutOfRangeException(nameof(value), $"{value:yyyy-MM-dd} is before B.S. {MinYear}");
        for (var i = 0; i < MonthDays.Length; i++)
            for (var m = 0; m < 12; m++)
            {
                if (days < MonthDays[i][m])
                    return (MinYear + i, m + 1, days + 1);
                days -= MonthDays[i][m];
            }
        throw new ArgumentOutOfRangeException(nameof(value), $"{value:yyyy-MM-dd} is after B.S. {MaxYear}");
    }

    /// <summary>B.S. (year, month, day) to A.D. date.</summary>
    public static DateOnly ToAd(int year, int month, int day)
    {
        if (year < MinYear || year > MaxYear || month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(year), $"B.S. {year}-{month} is outside the supported range");
        var months = MonthDays[year - MinYear];
        if (day < 1 || day > months[month - 1])
            throw new ArgumentOutOfRangeException(nameof(day), $"Invalid B.S. date {year}-{month}-{day}");
        var days = MonthDays.Take(year - MinYear).Sum(y => y.Sum()) + months.Take(month - 1).Sum() + day - 1;
        return Epoch.AddDays(days);
    }

    public static int DaysInMonth(int year, int month) => MonthDays[year - MinYear][month - 1];

    /// <summary>"2083-06-10".</summary>
    public static string Format(DateOnly ad)
    {
        var (y, m, d) = FromAd(ad);
        return $"{y:0000}-{m:00}-{d:00}";
    }

    /// <summary>Nepali fiscal year (Shrawan 1 to Asar end), e.g. "2083-84".</summary>
    public static string FiscalYear(DateOnly ad)
    {
        var (y, m, _) = FromAd(ad);
        var start = m >= 4 ? y : y - 1;
        return $"{start}-{(start + 1) % 100:00}";
    }
}
