using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Products;

namespace Accounting.Core.Inventory;

/// <summary>Read-side stock queries (register, ledger, low stock), ported from inventory/selectors.py.</summary>
public static class StockQueries
{
    public sealed record RegisterFilter
    {
        public string? Search { get; init; }
        public bool? IsActive { get; init; }
        /// <summary>IN_STOCK, LOW_STOCK or OUT_OF_STOCK.</summary>
        public string? Status { get; init; }
        /// <summary>Stock is the historical closing stock at this date.</summary>
        public DateOnly? AsOf { get; init; }
        /// <summary>Purchased/sold totals only include movements from this date.</summary>
        public DateOnly? Start { get; init; }
        public long? ProductId { get; init; }
        /// <summary>"name", "-current_stock", ... (name, sku_code, current_stock, cost_value, retail_value, total_sold, total_purchased).</summary>
        public string? Ordering { get; init; }
    }

    private static readonly Dictionary<string, string> RegisterOrdering = new()
    {
        ["name"] = "name COLLATE NOCASE", ["sku_code"] = "sku_code", ["current_stock"] = "current_stock", ["cost_value"] = "current_stock * purchase_price",
        ["retail_value"] = "current_stock * selling_price", ["total_sold"] = "total_sold", ["total_purchased"] = "total_purchased",
    };

    private static string OrderBy(string? ordering) =>
        ordering is { } o && RegisterOrdering.TryGetValue(o.TrimStart('-'), out var column)
            ? $"{column}{(o.StartsWith('-') ? " DESC" : "")}, id"
            : "name COLLATE NOCASE, id";

    // Per product: purchased/sold/adjusted totals in the period, stock up to as_of, valuation, status.
    private const string RegisterSql =
        """
        WITH totals AS (
            SELECT p.*,
                COALESCE(SUM(CASE WHEN m.transaction_type = 'PURCHASE' AND in_period THEN m.quantity_in END), 0)
              - COALESCE(SUM(CASE WHEN m.transaction_type = 'PURCHASE_CANCEL' AND in_period THEN m.quantity_out END), 0) AS total_purchased,
                COALESCE(SUM(CASE WHEN m.transaction_type = 'SALE' AND in_period THEN m.quantity_out END), 0)
              - COALESCE(SUM(CASE WHEN m.transaction_type = 'SALE_CANCEL' AND in_period THEN m.quantity_in END), 0) AS total_sold,
                COALESCE(SUM(CASE WHEN m.transaction_type = 'ADJUSTMENT' AND in_period THEN m.quantity_in - m.quantity_out END), 0) AS total_adjusted,
                COALESCE(SUM(m.quantity_in - m.quantity_out), 0) AS current_stock
            FROM products p
            LEFT JOIN (
                SELECT *, (@start IS NULL OR date >= @start) AS in_period FROM stock_movements
                WHERE @asOf IS NULL OR date <= @asOf
            ) m ON m.product_id = p.id
            WHERE (@isActive IS NULL OR p.is_active = @isActive) AND (@productId IS NULL OR p.id = @productId)
              AND (@search IS NULL OR p.name LIKE @search OR p.sku_code LIKE @search)
            GROUP BY p.id
        ), register AS (
            SELECT *,
                CASE WHEN current_stock <= 0 THEN 'OUT_OF_STOCK'
                     WHEN current_stock <= reorder_level THEN 'LOW_STOCK'
                     ELSE 'IN_STOCK' END AS stock_status
            FROM totals
        )
        """;

    public static Page<StockRegisterRow> Register(Tx tx, RegisterFilter filter, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        var args = Args(filter, page);
        const string where = "WHERE (@status IS NULL OR stock_status = @status)";
        var count = tx.Scalar<long>($"{RegisterSql} SELECT COUNT(*) FROM register {where}", args);
        var rows = tx.Rows($"{RegisterSql} SELECT * FROM register {where} ORDER BY {OrderBy(filter.Ordering)} LIMIT @limit OFFSET @offset", args)
            .Select(MapRegister).ToList();
        return new Page<StockRegisterRow>(rows, (int)count, page.Page, page.Size);
    }

    /// <summary>Totals over every product matching the filter.</summary>
    public static StockRegisterSummary Summary(Tx tx, RegisterFilter filter)
    {
        var rows = tx.Rows($"{RegisterSql} SELECT * FROM register WHERE (@status IS NULL OR stock_status = @status)", Args(filter, PageRequest.First))
            .Select(MapRegister).ToList();
        return new StockRegisterSummary(
            rows.Count,
            rows.Sum(r => r.CostValue),
            rows.Sum(r => r.RetailValue),
            rows.Count(r => r.StockStatus == StockStatus.LowStock),
            rows.Count(r => r.StockStatus == StockStatus.OutOfStock));
    }

    public static IReadOnlyList<StockRegisterRow> LowStock(Tx tx, int? limit = null)
    {
        var rows = tx.Rows($"{RegisterSql} SELECT * FROM register WHERE stock_status IN ('LOW_STOCK', 'OUT_OF_STOCK') ORDER BY current_stock, name",
                Args(new RegisterFilter { IsActive = true }, PageRequest.First))
            .Select(MapRegister);
        return (limit is { } n ? rows.Take(n) : rows).ToList();
    }

    private static object Args(RegisterFilter f, PageRequest page) => new
    {
        search = ProductService.LikePattern(f.Search),
        isActive = f.IsActive,
        status = f.Status,
        productId = f.ProductId,
        asOf = f.AsOf,
        start = f.Start,
        limit = page.Size,
        offset = page.Offset,
    };

    private static StockRegisterRow MapRegister(Row r)
    {
        var stock = r.Qty("current_stock");
        var cost = r.Money("purchase_price");
        var retail = r.Money("selling_price");
        return new StockRegisterRow(
            r.Long("id"), r.Str("name"), r.Str("sku_code"), Units.Parse(r.Str("unit")).Display(), r.Qty("reorder_level"),
            cost, retail, r.Bool("is_active"), r.Qty("total_purchased"), r.Qty("total_sold"), r.Qty("total_adjusted"),
            stock, Money.Round(stock * cost), Money.Round(stock * retail), r.Str("stock_status"));
    }

    public sealed record LedgerFilter
    {
        public long? ProductId { get; init; }
        public string? TransactionType { get; init; }
        public DateOnly? Start { get; init; }
        public DateOnly? End { get; init; }
        /// <summary>Matches reference number, product name or SKU.</summary>
        public string? Search { get; init; }
    }

    /// <summary>
    /// Movements in (date, id) order. Each row's closing quantity sums every movement of the same
    /// product up to and including that row, so it is correct whatever filters are applied.
    /// </summary>
    public static Page<StockLedgerRow> Ledger(Tx tx, LedgerFilter filter, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        const string where =
            """
            WHERE (@productId IS NULL OR m.product_id = @productId)
              AND (@type IS NULL OR m.transaction_type = @type)
              AND (@start IS NULL OR m.date >= @start)
              AND (@end IS NULL OR m.date <= @end)
              AND (@search IS NULL OR m.reference_number LIKE @search OR p.name LIKE @search OR p.sku_code LIKE @search)
            """;
        var args = new
        {
            productId = filter.ProductId,
            type = filter.TransactionType,
            start = filter.Start,
            end = filter.End,
            search = ProductService.LikePattern(filter.Search),
            limit = page.Size,
            offset = page.Offset,
        };
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM stock_movements m JOIN products p ON p.id = m.product_id {where}", args);
        var rows = tx.Rows(
            $"""
            SELECT m.*, p.name AS product_name, p.sku_code, p.unit, u.username AS created_by_name,
                (SELECT SUM(x.quantity_in - x.quantity_out) FROM stock_movements x
                 WHERE x.product_id = m.product_id AND (x.date < m.date OR (x.date = m.date AND x.id <= m.id))) AS closing_quantity
            FROM stock_movements m
            JOIN products p ON p.id = m.product_id
            LEFT JOIN users u ON u.id = m.created_by
            {where}
            ORDER BY m.date, m.id LIMIT @limit OFFSET @offset
            """, args).Select(MapLedger).ToList();
        return new Page<StockLedgerRow>(rows, (int)count, page.Page, page.Size);
    }

    private static StockLedgerRow MapLedger(Row r)
    {
        var closing = r.Qty("closing_quantity");
        var qin = r.Qty("quantity_in");
        var qout = r.Qty("quantity_out");
        return new StockLedgerRow(
            r.Long("id"), r.Date("date"), r.Long("product_id"), r.Str("product_name"), r.Str("sku_code"),
            Units.Parse(r.Str("unit")).Display(), r.Str("transaction_type"), r.Str("reference_type"), r.NullableLong("reference_id"),
            r.Str("reference_number"), closing - qin + qout, qin, qout, closing, r.Qty("running_balance"), r.Str("notes"),
            r.Timestamp("created_at"), r.NullableStr("created_by_name"));
    }
}
