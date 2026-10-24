using Accounting.Core.Common;

namespace Accounting.Tests;

// Ported from backend/tests/test_nepali_date.py.
public class NepaliDateTests
{
    [Theory]
    [InlineData(1943, 4, 14, 2000, 1, 1)]
    [InlineData(2026, 10, 13, 2083, 6, 27)]
    [InlineData(2026, 7, 17, 2083, 4, 1)] // Shrawan 1: fiscal year start
    [InlineData(2024, 4, 13, 2081, 1, 1)] // Nepali new year 2081
    public void KnownDates(int y, int m, int d, int by, int bm, int bd)
    {
        var ad = new DateOnly(y, m, d);
        Assert.Equal((by, bm, bd), NepaliDate.FromAd(ad));
        Assert.Equal(ad, NepaliDate.ToAd(by, bm, bd));
    }

    [Fact]
    public void RoundTripWholeRange()
    {
        var day = new DateOnly(1943, 4, 14);
        while (true)
        {
            (int, int, int) bs;
            try { bs = NepaliDate.FromAd(day); }
            catch (ArgumentOutOfRangeException) { break; }
            Assert.Equal(day, NepaliDate.ToAd(bs.Item1, bs.Item2, bs.Item3));
            day = day.AddDays(1);
        }
        Assert.Equal(NepaliDate.MaxYear, NepaliDate.FromAd(day.AddDays(-1)).Year);
    }

    [Theory]
    [InlineData(1999, 1, 1)]
    [InlineData(2083, 13, 1)]
    [InlineData(2083, 6, 0)]
    [InlineData(2083, 6, 32)]
    public void InvalidBsDates(int y, int m, int d) => Assert.Throws<ArgumentOutOfRangeException>(() => NepaliDate.ToAd(y, m, d));

    [Fact]
    public void FiscalYearAndFormat()
    {
        Assert.Equal("2083-84", NepaliDate.FiscalYear(new DateOnly(2026, 7, 17)));
        Assert.Equal("2082-83", NepaliDate.FiscalYear(new DateOnly(2026, 7, 16)));
        Assert.Equal("2083-06-27", NepaliDate.Format(new DateOnly(2026, 10, 13)));
    }
}
