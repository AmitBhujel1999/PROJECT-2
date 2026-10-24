namespace Accounting.Core;

/// <summary>
/// Decimal helpers. Financial values are NEVER doubles in this system.
///
/// Precision policy (same as the Django backend):
/// * Money: 2 decimal places, round half away from zero (commercial rounding).
/// * Quantity: 3 decimal places (supports Kg / Ltr / Meter).
/// * Rates (tax / percentage discount): 2 decimal places.
/// Rounding is applied per line (gross, discount, tax), and document totals are
/// the sum of already-rounded line values, so printed lines always add up.
/// </summary>
public static class Money
{
    public const decimal Hundred = 100m;

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal Qty(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    public static decimal Rate(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Money stored in SQLite as integer paisa, so no rounding drift is possible.</summary>
    public static long ToPaisa(decimal value) => (long)(Round(value) * 100m);

    public static decimal FromPaisa(long paisa) => paisa / 100m;

    /// <summary>Quantities are stored as integer thousandths.</summary>
    public static long ToMilli(decimal value) => (long)(Qty(value) * 1000m);

    public static decimal FromMilli(long milli) => milli / 1000m;
}
