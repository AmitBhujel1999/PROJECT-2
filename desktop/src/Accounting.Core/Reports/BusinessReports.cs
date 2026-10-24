using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Expenses;
using Accounting.Core.Inventory;
using Accounting.Core.Journal;
using Accounting.Core.Payments;
using Accounting.Core.Trade;

namespace Accounting.Core.Reports;

public sealed record TradeReportRow(TradeDocument Document, int Items, decimal Quantity);

public sealed record TradeSummary(
    int TotalDocuments, decimal GrossAmount, decimal ItemDiscount, decimal InvoiceDiscount, decimal TaxableAmount,
    decimal TaxAmount, decimal TotalAmount, decimal AmountPaid, decimal Outstanding)
{
    public decimal TotalDiscount => Money.Round(ItemDiscount + InvoiceDiscount);
}

public sealed record CategoryTotal(long CategoryId, string CategoryName, int Count, decimal Amount, decimal TaxAmount, decimal TotalAmount, decimal JournalAmount)
{
    public decimal TotalWithJournal => Money.Round(TotalAmount + JournalAmount);
}

public sealed record ExpenseSummary(int TotalExpenses, decimal Amount, decimal TaxAmount, decimal TotalAmount, IReadOnlyList<CategoryTotal> ByCategory)
{
    public decimal JournalAmount => Money.Round(ByCategory.Sum(c => c.JournalAmount));
    public decimal TotalWithJournal => Money.Round(TotalAmount + JournalAmount);
}

public sealed record OutstandingRow(AgingDocument Document, long PartyId, string PartyName);

public sealed record OutstandingReport(DateOnly AsOf, IReadOnlyList<OutstandingRow> Rows)
{
    public decimal Total => Money.Round(Rows.Sum(r => r.Document.TotalAmount));
    public decimal Outstanding => Money.Round(Rows.Sum(r => r.Document.Outstanding));
    public decimal Overdue => Money.Round(Rows.Where(r => r.Document.DueDate < AsOf).Sum(r => r.Document.Outstanding));
}

public sealed record TrendPoint(DateOnly Date, decimal Sales, decimal Purchases, decimal Receipts, decimal Payments, decimal Expenses);

public sealed record TopProduct(long ProductId, string Name, string SkuCode, decimal Quantity, decimal Amount);

/// <summary>Dashboard figures. Finance figures (receivable/payable) are null for users without reports.view.</summary>
public sealed record DashboardData(
    DateOnly Start, DateOnly End, bool Monthly, IReadOnlyDictionary<string, decimal> Cards, int TotalCustomers, int TotalVendors,
    int LowStockCount, IReadOnlyList<TrendPoint> Trend, IReadOnlyList<TopProduct> TopProducts,
    IReadOnlyList<StockRegisterRow> StockValueByProduct, IReadOnlyList<StockRegisterRow> LowStock, IReadOnlyList<TradeDocument> RecentSales);

/// <summary>Sales, purchase, expense and outstanding reports and the dashboard, ported from backend/apps/reports/views.py.</summary>
public static class BusinessReports
{
    // ------------------------------------------------------------------ sales & purchase reports

    /// <summary>Report filter: Status null means active documents only; "ALL" includes cancelled.</summary>
    private static TradeFilter ForReport(TradeFilter f) => f with { Status = f.Status switch { null => DocumentStatus.Active, "ALL" => null, var s => s } };

    public static TradeSummary TradeSummary(Tx tx, TradeKind kind, TradeFilter filter)
    {
        var all = TradeService.List(tx, kind, ForReport(filter), new PageRequest(1, 100));
        var docs = new List<TradeDocument>(all.Results);
        for (var page = 2; page <= all.TotalPages; page++)
            docs.AddRange(TradeService.List(tx, kind, ForReport(filter), new PageRequest(page, 100)).Results);
        var active = docs.Where(d => !d.IsCancelled).ToList();
        return new TradeSummary(docs.Count, Money.Round(docs.Sum(d => d.Subtotal)), Money.Round(docs.Sum(d => d.ItemDiscountTotal)),
            Money.Round(docs.Sum(d => d.DiscountAmount)), Money.Round(docs.Sum(d => d.TaxableAmount)), Money.Round(docs.Sum(d => d.TaxAmount)),
            Money.Round(docs.Sum(d => d.TotalAmount)), Money.Round(active.Sum(d => d.AmountPaid)), Money.Round(active.Sum(d => d.BalanceDue)));
    }

    /// <summary>Documents newest first, with line count and total quantity.</summary>
    public static Page<TradeReportRow> TradeRows(Tx tx, TradeKind kind, TradeFilter filter, PageRequest? page = null)
    {
        var docs = TradeService.List(tx, kind, ForReport(filter), page);
        var ids = docs.Results.Select(d => d.Id).ToList();
        var counts = tx.Rows($"SELECT document_id, COUNT(*) AS n, SUM(quantity) AS q FROM {kind.ItemsTable} WHERE document_id IN @ids GROUP BY document_id", new { ids })
            .ToDictionary(r => r.Long("document_id"), r => ((int)r.Long("n"), r.Qty("q")));
        return new Page<TradeReportRow>(docs.Results.Select(d => new TradeReportRow(d, counts.GetValueOrDefault(d.Id).Item1, counts.GetValueOrDefault(d.Id).Item2)).ToList(),
            docs.Count, docs.PageNumber, docs.PageSize);
    }

    /// <summary>Every matching row (for export).</summary>
    public static IReadOnlyList<TradeReportRow> AllTradeRows(Tx tx, TradeKind kind, TradeFilter filter)
    {
        var rows = new List<TradeReportRow>();
        for (var page = 1; ; page++)
        {
            var p = TradeRows(tx, kind, filter, new PageRequest(page, 100));
            rows.AddRange(p.Results);
            if (page >= p.TotalPages)
                return rows;
        }
    }

    // ------------------------------------------------------------------ expense report

    /// <summary>Totals and a per-category breakdown, adding journal amounts on categories when the filter allows it.</summary>
    public static ExpenseSummary ExpenseSummary(Tx tx, ExpenseFilter filter)
    {
        var expenses = ExpenseService.All(tx, filter);
        // Journal amounts have no vendor/account/search, so they only join an unfiltered (or category-only) view.
        var addJournal = filter.VendorId is null && filter.AccountId is null && filter.Search is null && filter.Status != DocumentStatus.Cancelled;
        var journal = addJournal ? JournalService.ExpenseCategoryTotals(tx, filter.Start, filter.End, filter.CategoryId).Where(j => j.Value != 0).ToDictionary() : [];
        var names = ExpenseService.Categories(tx).ToDictionary(c => c.Id, c => c.Name);
        var byCategory = expenses.GroupBy(e => e.CategoryId)
            .Select(g => new CategoryTotal(g.Key, names[g.Key], g.Count(), Money.Round(g.Sum(e => e.Amount)), Money.Round(g.Sum(e => e.TaxAmount)),
                Money.Round(g.Sum(e => e.TotalAmount)), journal.GetValueOrDefault(g.Key)))
            .ToList();
        byCategory.AddRange(journal.Where(j => byCategory.All(c => c.CategoryId != j.Key))
            .Select(j => new CategoryTotal(j.Key, names.GetValueOrDefault(j.Key, ""), 0, 0, 0, 0, j.Value)));
        return new ExpenseSummary(expenses.Count, Money.Round(expenses.Sum(e => e.Amount)), Money.Round(expenses.Sum(e => e.TaxAmount)),
            Money.Round(expenses.Sum(e => e.TotalAmount)), byCategory.OrderByDescending(c => c.TotalWithJournal).ThenBy(c => c.CategoryName).ToList());
    }

    // ------------------------------------------------------------------ receivables / payables report

    public static OutstandingReport Outstanding(Tx tx, AccountConfig cfg, DateOnly asOf, long? partyId = null, bool overdueOnly = false) =>
        new(asOf, PartyLedgers.OutstandingDocuments(tx, cfg, asOf, partyId, overdueOnly).Select(x => new OutstandingRow(x.Document, x.PartyId, x.PartyName)).ToList());

    // ------------------------------------------------------------------ dashboard

    /// <summary>Dashboard for the period (default: the last 30 days). Periods over 92 days are shown by month.</summary>
    public static DashboardData Dashboard(Tx tx, bool canSeeFinance, bool canSeeExpenses, DateOnly? start = null, DateOnly? end = null)
    {
        var today = tx.Today;
        var e = end ?? today;
        var s = start ?? e.AddDays(-29);
        if (s > e)
            (s, e) = (e, s);
        var monthly = e.DayNumber - s.DayNumber > 92;

        decimal Day(string table, string column, DateOnly d) =>
            Money.FromPaisa(tx.Scalar<long>($"SELECT COALESCE(SUM({column}), 0) FROM {table} WHERE status = 'ACTIVE' AND date = @d", new { d }));
        decimal Period(string table, string column) =>
            Money.FromPaisa(tx.Scalar<long>($"SELECT COALESCE(SUM({column}), 0) FROM {table} WHERE status = 'ACTIVE' AND date BETWEEN @s AND @e", new { s, e }));

        var register = StockQueries.Register(tx, new StockQueries.RegisterFilter { IsActive = true }, new PageRequest(1, 100));
        var stockRows = register.Results.ToList();
        for (var page = 2; page <= register.TotalPages; page++)
            stockRows.AddRange(StockQueries.Register(tx, new StockQueries.RegisterFilter { IsActive = true }, new PageRequest(page, 100)).Results);

        var cards = new Dictionary<string, decimal>
        {
            ["todays_sales"] = Day("sales", "total_amount", today),
            ["todays_purchases"] = Day("purchases", "total_amount", today),
            ["todays_receipts"] = Day("customer_receipts", "amount", today),
            ["todays_payments"] = Day("vendor_payments", "amount", today),
            ["period_sales"] = Period("sales", "total_amount"),
            ["period_purchases"] = Period("purchases", "total_amount"),
            ["period_receipts"] = Period("customer_receipts", "amount"),
            ["period_payments"] = Period("vendor_payments", "amount"),
            ["stock_value"] = Money.Round(stockRows.Sum(r => r.CostValue)),
            ["stock_retail_value"] = Money.Round(stockRows.Sum(r => r.RetailValue)),
        };
        if (canSeeExpenses)
        {
            cards["todays_expenses"] = Day("expenses", "total_amount", today);
            cards["period_expenses"] = Period("expenses", "total_amount");
        }
        if (canSeeFinance)
        {
            var receivable = PartyLedgers.Overview(tx, AccountConfig.Receivable);
            var payable = PartyLedgers.Overview(tx, AccountConfig.Payable);
            cards["receivables"] = receivable.Outstanding;
            cards["overdue_receivables"] = receivable.Overdue;
            cards["customer_advances"] = receivable.Unallocated;
            cards["payables"] = payable.Outstanding;
            cards["overdue_payables"] = payable.Overdue;
            cards["vendor_advances"] = payable.Unallocated;
        }

        // Trend: one point per day, or per month for long periods.
        DateOnly Bucket(DateOnly d) => monthly ? new DateOnly(d.Year, d.Month, 1) : d;
        Dictionary<DateOnly, decimal> Series(string table, string column) =>
            tx.Rows($"SELECT date, SUM({column}) AS t FROM {table} WHERE status = 'ACTIVE' AND date BETWEEN @s AND @e GROUP BY date", new { s, e })
                .GroupBy(r => Bucket(r.Date("date"))).ToDictionary(g => g.Key, g => Money.Round(g.Sum(r => r.Money("t"))));
        var (sales, purchases, receipts, payments, expenses) = (Series("sales", "total_amount"), Series("purchases", "total_amount"),
            Series("customer_receipts", "amount"), Series("vendor_payments", "amount"), Series("expenses", "total_amount"));
        var trend = new List<TrendPoint>();
        for (var d = Bucket(s); d <= e; d = monthly ? d.AddMonths(1) : d.AddDays(1))
            trend.Add(new TrendPoint(d, sales.GetValueOrDefault(d), purchases.GetValueOrDefault(d), receipts.GetValueOrDefault(d),
                payments.GetValueOrDefault(d), expenses.GetValueOrDefault(d)));

        var top = tx.Rows(
                """
                SELECT i.product_id, p.name, p.sku_code, SUM(i.quantity) AS qty, SUM(i.taxable_amount) AS amount
                FROM sale_items i JOIN sales d ON d.id = i.document_id JOIN products p ON p.id = i.product_id
                WHERE d.status = 'ACTIVE' AND d.date BETWEEN @s AND @e
                GROUP BY i.product_id ORDER BY amount DESC LIMIT 10
                """, new { s, e })
            .Select(r => new TopProduct(r.Long("product_id"), r.Str("name"), r.Str("sku_code"), r.Qty("qty"), r.Money("amount"))).ToList();
        var recent = TradeService.List(tx, TradeKind.Sale, new TradeFilter { Status = DocumentStatus.Active }, new PageRequest(1, 25)).Results.Take(5).ToList();
        var low = stockRows.Where(r => r.StockStatus != StockStatus.InStock).OrderBy(r => r.CurrentStock).ThenBy(r => r.Name).ToList();
        return new DashboardData(s, e, monthly, cards,
            (int)tx.Scalar<long>("SELECT COUNT(*) FROM parties WHERE type = 'CUSTOMER' AND is_active = 1"),
            (int)tx.Scalar<long>("SELECT COUNT(*) FROM parties WHERE type = 'VENDOR' AND is_active = 1"),
            low.Count, trend, top,
            stockRows.Where(r => r.CurrentStock > 0).OrderByDescending(r => r.CostValue).Take(10).ToList(),
            low.Take(10).ToList(), recent);
    }
}
