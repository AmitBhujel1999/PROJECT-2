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
