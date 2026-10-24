using Accounting.Core;
using Accounting.Core.Expenses;
using Accounting.Core.Journal;
using Accounting.Core.Payments;
using Accounting.Core.Products;
using Accounting.Core.Reports;
using Accounting.Core.Trade;
using static Accounting.Tests.TestDb;

namespace Accounting.Tests;

// Ported from backend/tests/test_reports.py (and the expense report parts of test_expenses.py).
public class ReportTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly Product _p;

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    public ReportTests()
    {
        _p = _t.Product("Report Item", stock: 1000, price: 100, cost: 50);
        _t.MakePurchase(_t.Vendor(), [Item(_p, 10, 50, DiscountType.Percentage, 10)], D(2026, 8, 1));
        _t.MakePurchase(_t.Vendor(), [Item(_p, 10, 50)], D(2026, 9, 1));
        _t.MakeSale(_t.Customer(), [Item(_p, 2, null, DiscountType.Fixed, 20)], D(2026, 8, 5));
        var customer2 = _t.Customer2().Id;
        _t.Write(tx => TradeService.Create(tx, _t.Admin, TradeKind.Sale, new DocumentInput
            { PartyId = customer2, Date = D(2026, 9, 5), Items = [Item(_p, 3)], DiscountType = DiscountType.Percentage, DiscountValue = 10 }));
        _t.MakeSale(_t.Customer(), [Item(_p, 1)], D(2026, 9, 10));
    }

    public void Dispose() => _t.Dispose();

    private TradeSummary Summary(TradeKind kind, TradeFilter? f = null) => _t.Read(tx => BusinessReports.TradeSummary(tx, kind, f ?? new TradeFilter()));
    private int Count(TradeFilter f) => _t.Read(tx => BusinessReports.TradeRows(tx, TradeKind.Sale, f)).Count;

    [Fact]
    public void SalesReportSummaryAndFilters()
    {
        var s = Summary(TradeKind.Sale);
        Assert.Equal(3, s.TotalDocuments);
        Assert.Equal(600.00m, s.GrossAmount);
        Assert.Equal(50.00m, s.TotalDiscount); // 20 item + 30 invoice
        Assert.Equal(550.00m, s.TaxableAmount);
        Assert.Equal(71.50m, s.TaxAmount);
        Assert.Equal(621.50m, s.TotalAmount);
        Assert.Equal(621.50m, s.Outstanding);
        var rows = _t.Read(tx => BusinessReports.TradeRows(tx, TradeKind.Sale, new TradeFilter())).Results;
        Assert.Equal(("INV-000003", 1, 1m), (rows[0].Document.Number, rows[0].Items, rows[0].Quantity)); // newest first
        Assert.Equal(2, Count(new TradeFilter { Start = D(2026, 9, 1), End = D(2026, 9, 30) }));
        Assert.Equal(2, Count(new TradeFilter { PartyId = _t.Customer().Id }));
        Assert.Equal(1, Count(new TradeFilter { Number = "000002" }));
        Assert.Equal(1, Count(new TradeFilter { Search = "xyz" }));
        Assert.Equal(0, Count(new TradeFilter { PaymentStatus = "PAID" }));
    }

    [Fact]
    public void ProductProfitUsesWeightedAverageCost()
    {
        // Received: opening 1,000 @ 50 + 10 @ 45 (10% discount) + 10 @ 50 = 50,950 for 1,020 units.
        // Sold: 6 units for 550.00 excl. VAT -> cost 6 x 49.95098 = 299.71, profit 250.29, margin 45.51%.
        var row = Assert.Single(_t.Read(tx => BusinessReports.ProductProfit(tx, null, D(2026, 12, 31))));
        Assert.Equal((6m, 550.00m, 299.71m, 250.29m, 45.51m), (row.QuantitySold, row.Sales, row.CostOfSales, row.Profit, row.MarginPercent));
        Assert.Equal(49.95m, row.AverageCost);

        // Only sales in the period; cost as of the period end.
        var sept = Assert.Single(_t.Read(tx => BusinessReports.ProductProfit(tx, D(2026, 9, 1), D(2026, 9, 30))));
        Assert.Equal(4m, sept.QuantitySold);
        Assert.Empty(_t.Read(tx => BusinessReports.ProductProfit(tx, D(2026, 9, 1), D(2026, 9, 30), "no such item")));

        // A cancelled sale no longer counts.
        var first = _t.Read(tx => BusinessReports.TradeRows(tx, TradeKind.Sale, new TradeFilter())).Results.Last().Document;
        _t.Write(tx => TradeService.Cancel(tx, _t.Admin, TradeKind.Sale, first.Id, "Mistake"));
        Assert.Equal(4m, Assert.Single(_t.Read(tx => BusinessReports.ProductProfit(tx, null, D(2026, 12, 31)))).QuantitySold);
    }

    [Fact]
    public void CancelledDocumentsOnlyWhenAskedFor()
    {
        var first = _t.Read(tx => BusinessReports.TradeRows(tx, TradeKind.Sale, new TradeFilter())).Results.Last().Document;
        _t.Write(tx => TradeService.Cancel(tx, _t.Admin, TradeKind.Sale, first.Id, "x"));
        Assert.Equal(2, Count(new TradeFilter()));
        Assert.Equal(3, Count(new TradeFilter { Status = "ALL" }));
        Assert.Equal(1, Count(new TradeFilter { Status = "CANCELLED" }));
    }

    [Fact]
    public void PurchaseReport()
    {
        var s = Summary(TradeKind.Purchase);
        Assert.Equal((2, 1000.00m, 50.00m, 123.50m, 1073.50m), (s.TotalDocuments, s.GrossAmount, s.TotalDiscount, s.TaxAmount, s.Outstanding));
    }

    [Fact]
    public void ExportRowsCoverEveryPage()
    {
        for (var i = 0; i < 120; i++)
            _t.MakeSale(_t.Customer(), [Item(_p, 1)], D(2026, 9, 20));
        Assert.Equal(123, _t.Read(tx => BusinessReports.AllTradeRows(tx, TradeKind.Sale, new TradeFilter())).Count);
        Assert.Equal(123, Summary(TradeKind.Sale).TotalDocuments);
    }

    [Fact]
    public void StockAndAgingReports()
    {
        var row = _t.Read(tx => Accounting.Core.Inventory.StockQueries.Register(tx, new())).Results[0];
        Assert.Equal((20m, 6m), (row.TotalPurchased, row.TotalSold));
        var aging = _t.Read(tx => PartyLedgers.Aging(tx, AccountConfig.Receivable, D(2026, 9, 27)));
        Assert.Equal(621.50m, aging.Totals.Total);
        Assert.Equal(["ABC Traders", "XYZ Store"], aging.Rows.Select(r => r.PartyName));
        Assert.Equal(1073.50m, _t.Read(tx => PartyLedgers.Aging(tx, AccountConfig.Payable, D(2026, 9, 27))).Totals.Total);
        // INV-1 (08-05, 30 days -> due 09-04) and INV-2 (09-05, 15 days -> due 09-20) are overdue
        var overdue = _t.Read(tx => BusinessReports.Outstanding(tx, AccountConfig.Receivable, D(2026, 9, 27), overdueOnly: true));
        Assert.Equal(2, overdue.Rows.Count);
        var all = _t.Read(tx => BusinessReports.Outstanding(tx, AccountConfig.Receivable, D(2026, 9, 27)));
        Assert.Equal((3, 621.50m), (all.Rows.Count, all.Outstanding));
        Assert.Equal(all.Overdue, overdue.Outstanding);
    }

    [Fact]
    public void Dashboard()
    {
        var d = _t.Read(tx => BusinessReports.Dashboard(tx, canSeeFinance: true, canSeeExpenses: true, D(2026, 8, 1), D(2026, 9, 30)));
        Assert.Equal(621.50m, d.Cards["period_sales"]);
        Assert.Equal(621.50m, d.Cards["receivables"]);
        Assert.Equal(2, d.TotalCustomers);
        Assert.Equal(61, d.Trend.Count);
        Assert.Equal("Report Item", d.TopProducts[0].Name);
        Assert.Equal("INV-000003", d.RecentSales[0].Number);
        var staff = _t.Read(tx => BusinessReports.Dashboard(tx, canSeeFinance: false, canSeeExpenses: false));
        Assert.DoesNotContain("receivables", staff.Cards.Keys);
        Assert.DoesNotContain("todays_expenses", staff.Cards.Keys);
        Assert.Equal(30, staff.Trend.Count);
        var yearly = _t.Read(tx => BusinessReports.Dashboard(tx, true, true, D(2026, 1, 1), D(2026, 9, 30)));
        Assert.True(yearly.Monthly);
        Assert.Equal(9, yearly.Trend.Count);
        Assert.Equal(621.50m, yearly.Trend.Sum(t => t.Sales));
    }

    [Fact]
    public void ExpenseReportSummaryBreakdownAndJournal()
    {
        var categories = _t.Read(tx => ExpenseService.Categories(tx)).ToDictionary(c => c.Name);
        ExpenseInput E(string category, decimal amount, decimal tax = 0) =>
            new() { Date = TestDb.Today, CategoryId = categories[category].Id, Description = "September rent", Amount = amount, TaxRate = tax };
        _t.Write(tx => ExpenseService.Create(tx, _t.Admin, E("Rent", 10000)));
        _t.Write(tx => ExpenseService.Create(tx, _t.Admin, E("Rent", 5000, 13)));
        _t.Write(tx => ExpenseService.Create(tx, _t.Admin, E("Utilities", 2000)));
        var cancelled = _t.Write(tx => ExpenseService.Create(tx, _t.Admin, E("Utilities", 999)));
        _t.Write(tx => ExpenseService.Cancel(tx, _t.Admin, cancelled.Id, "mistake"));
        var s = _t.Read(tx => BusinessReports.ExpenseSummary(tx, new ExpenseFilter()));
        Assert.Equal((3, 17000.00m, 650.00m, 17650.00m), (s.TotalExpenses, s.Amount, s.TaxAmount, s.TotalAmount));
        Assert.Equal([("Rent", 15650.00m), ("Utilities", 2000.00m)], s.ByCategory.Select(c => (c.CategoryName, c.TotalAmount)));
        Assert.Equal(4, _t.Read(tx => BusinessReports.ExpenseSummary(tx, new ExpenseFilter { Status = "ALL" })).TotalExpenses);

        // A journal voucher on a category joins the breakdown.
        var misc = categories["Miscellaneous"].Id;
        _t.Write(tx =>
        {
            var capital = JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Capital", "CAPITAL"));
            return JournalService.CreateVoucher(tx, _t.Admin, TestDb.Today, "Depreciation", [new($"expense:{misc}", 300), new($"ledger:{capital.Id}", Credit: 300)]);
        });
        s = _t.Read(tx => BusinessReports.ExpenseSummary(tx, new ExpenseFilter { Start = D(2026, 9, 1), End = D(2026, 9, 30) }));
        Assert.Equal(300.00m, s.JournalAmount);
        Assert.Equal(300.00m, s.ByCategory.Single(c => c.CategoryName == "Miscellaneous").TotalWithJournal);
        Assert.Equal(0.00m, _t.Read(tx => BusinessReports.ExpenseSummary(tx, new ExpenseFilter { Search = "rent" })).JournalAmount);
    }
}
