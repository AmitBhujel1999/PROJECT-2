using System.Globalization;

namespace Accounting.Core.Common;

/// <summary>Number and date text shared by screens, exports and printouts: lakh grouping (12,34,567.89), A.D. or B.S. dates.</summary>
public static class Format
{
    private static readonly NumberFormatInfo Lakh = new()
    {
        NumberGroupSizes = [3, 2],
        NumberGroupSeparator = ",",
        NumberDecimalSeparator = ".",
        NegativeSign = "-",
    };

    public static string Money(decimal value) => value.ToString("N2", Lakh);

    public static string Qty(decimal value) => value.ToString("#,##0.###", Lakh);

    private static readonly string[] Ones =
    [
        "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];
    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    /// <summary>"Rupees One Lakh Twenty Thousand Five and Fifty Paisa Only" (lakh/crore grouping, as on Nepali bills).</summary>
    public static string AmountInWords(decimal amount)
    {
        amount = Math.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        var rupees = (long)decimal.Truncate(amount);
        var paisa = (int)((amount - rupees) * 100);
        var text = "Rupees " + (rupees == 0 ? "Zero" : Words(rupees));
        if (paisa > 0)
            text += " and " + Words(paisa) + " Paisa";
        return text + " Only";
    }

    private static string Words(long n)
    {
        var parts = new List<string>();
        if (n >= 10_000_000)
            parts.Add(Words(n / 10_000_000) + " Crore");
        foreach (var (size, name) in new[] { (100_000L, "Lakh"), (1_000L, "Thousand") })
            if (n / size % 100 is var count and > 0)
                parts.Add(UpTo99(count) + " " + name);
        if (n / 100 % 10 is var hundreds and > 0)
            parts.Add(Ones[hundreds] + " Hundred");
        if (n % 100 > 0)
            parts.Add(UpTo99(n % 100));
        return string.Join(" ", parts);
    }

    private static string UpTo99(long n) => n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 > 0 ? "-" + Ones[n % 10] : "");

    public static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"2083-06-10" in B.S. or "2026-09-26" in A.D.; dates outside the B.S. table fall back to A.D.</summary>
    public static string Date(DateOnly value, bool bs)
    {
        if (!bs)
            return Iso(value);
        try { return NepaliDate.Format(value); }
        catch (ArgumentOutOfRangeException) { return Iso(value) + " AD"; }
    }

    public static string Date(DateOnly? value, bool bs) => value is { } d ? Date(d, bs) : "";

    public static string Status(string code) => code switch
    {
        "ACTIVE" => "Active",
        "CANCELLED" => "Cancelled",
        "PAID" => "Paid",
        "PARTIAL" => "Partial",
        "UNPAID" => "Unpaid",
        "IN_STOCK" => "In stock",
        "LOW_STOCK" => "Low stock",
        "OUT_OF_STOCK" => "Out of stock",
        _ => code,
    };
}
