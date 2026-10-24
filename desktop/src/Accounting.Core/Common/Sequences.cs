using Accounting.Core.Data;

namespace Accounting.Core.Common;

/// <summary>
/// Gap-free document numbering (INV-000001, ...). The counter moves inside the
/// caller's write transaction, so a failed save rolls the number back too, and
/// the UNIQUE constraint on each document number is a second line of defence.
/// </summary>
public static class Sequences
{
    public const string Sale = "sale";
    public const string Purchase = "purchase";
    public const string Receipt = "customer_receipt";
    public const string Payment = "vendor_payment";
    public const string Adjustment = "stock_adjustment";
    public const string Expense = "expense";
    public const string Contra = "contra";
    public const string Journal = "journal";

    public static string Next(Tx tx, string key)
    {
        tx.RequireWrite();
        var row = tx.Row("UPDATE document_sequences SET last_number = last_number + 1 WHERE key = @key RETURNING prefix, padding, last_number",
            new { key }) ?? throw new InvalidOperationException($"Unknown document sequence '{key}'.");
        return $"{row.Str("prefix")}-{row.Long("last_number").ToString().PadLeft((int)row.Long("padding"), '0')}";
    }
}
