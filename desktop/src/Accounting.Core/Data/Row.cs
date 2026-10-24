using System.Globalization;
using Dapper;

namespace Accounting.Core.Data;

/// <summary>
/// One result row with typed getters that undo the storage scaling
/// (paisa, thousandths, hundredths). Entities are mapped by hand from rows so a
/// money column can never be read as a plain number by mistake.
/// </summary>
public sealed class Row(IDictionary<string, object?> values)
{
    private object? this[string column] =>
        values.TryGetValue(column, out var v) ? v : throw new KeyNotFoundException($"Column '{column}' is not in the result.");

    public bool Has(string column) => values.ContainsKey(column);

    public long Long(string column) => this[column] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
    public long? NullableLong(string column) => this[column] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
    public string Str(string column) => this[column] as string ?? "";
    public string? NullableStr(string column) => this[column] as string;
    public bool Bool(string column) => Long(column) != 0;
    public DateOnly Date(string column) => DateOnly.ParseExact(Str(column), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public DateOnly? NullableDate(string column) => NullableStr(column) is { } s ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    public DateTime Timestamp(string column) => DateTime.Parse(Str(column), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>Money column stored as paisa.</summary>
    public decimal Money(string column) => Core.Money.FromPaisa(Long(column));
    /// <summary>Quantity column stored as thousandths.</summary>
    public decimal Qty(string column) => Core.Money.FromMilli(Long(column));
    /// <summary>Rate column stored as hundredths of a percent.</summary>
    public decimal Rate(string column) => Core.Money.FromPaisa(Long(column));
}

public static class RowQueries
{
    public static IEnumerable<Row> Rows(this Tx tx, string sql, object? param = null) =>
        tx.Connection.Query(sql, param, tx.Transaction).Select(r => new Row((IDictionary<string, object?>)r));

    public static Row? Row(this Tx tx, string sql, object? param = null) => tx.Rows(sql, param).FirstOrDefault();
}
