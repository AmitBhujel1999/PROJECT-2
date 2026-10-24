using Accounting.Core;
using Accounting.Core.CashBank;
using Accounting.Core.Common;
using Accounting.Core.Expenses;
using Accounting.Core.Inventory;
using Accounting.Core.Journal;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Products;
using Accounting.Core.Reports;
using Accounting.Core.Trade;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>/api/reports/*, /api/dashboard/ and /api/search/. Every report exports with ?export=csv|pdf.</summary>
public static class ReportsApi
{
    // ------------------------------------------------------------------ export plumbing

    public static IResult Export(Api a, string kind, ReportTable table)
    {
        var settings = a.Settings;
        var today = a.Db.Today();
        return kind == "pdf"
            ? Api.File(Pdf.Write(table, settings), "application/pdf", table.FileName("pdf", today), inline: a.Q("inline") == "1")
            : Api.File(Csv.Write(table, settings), "text/csv; charset=utf-8", table.FileName("csv", today));
    }

    private static string F(decimal v) => Format.Money(v);
    private static string Date(Api a, DateOnly d) => Format.Date(d, a.Settings.DateSystem == "BS");

    private static (string, string)[] Period(Api a, DateOnly? start, DateOnly? end) =>
    [
        .. start is { } s ? new[] { ("From", Date(a, s)) } : [],
        .. end is { } e ? new[] { ("To", Date(a, e)) } : [],
    ];

    private static readonly Dictionary<string, Func<ProductProfitRow, object>> ProfitOrder = new()
    {
        ["name"] = r => r.Name, ["sku_code"] = r => r.SkuCode, ["quantity"] = r => r.QuantitySold, ["sales"] = r => r.Sales,
        ["cost"] = r => r.CostOfSales, ["profit"] = r => r.Profit, ["margin"] = r => r.MarginPercent ?? decimal.MinValue,
    };

    /// <summary>Product profitability for the request: period (default: this fiscal year to date), search, sort.</summary>
    private static (List<ProductProfitRow> Rows, DateOnly Start, DateOnly End) ProductProfit(Api a)
    {
        var end = a.Date("end_date") ?? a.Db.Today();
        var (y, m, _) = NepaliDate.FromAd(end);
        var start = a.Date("start_date") ?? NepaliDate.ToAd(m >= 4 ? y : y - 1, 4, 1);
        if (start > end)
            throw new ValidationError("start_date", "Start date must be on or before the end date.");
        var rows = a.Read(tx => BusinessReports.ProductProfit(tx, start, end, a.Q("search")));
        var ordering = a.Q("ordering") ?? "-profit";
        var key = ordering.TrimStart('-');
        if (ProfitOrder.TryGetValue(key, out var by))
            rows = (ordering.StartsWith('-') ? rows.OrderByDescending(by) : rows.OrderBy(by)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return (rows, start, end);
    }

    private static (decimal Sales, decimal Cost, decimal Profit, decimal? Margin) ProfitTotals(List<ProductProfitRow> rows)
    {
        var sales = rows.Sum(r => r.Sales);
        var cost = rows.Sum(r => r.CostOfSales);
        return (sales, cost, sales - cost, sales == 0 ? null : Math.Round((sales - cost) * 100m / sales, 2, MidpointRounding.AwayFromZero));
    }

    public static ReportTable ProductProfitTable(Api a)
    {
        var (rows, start, end) = ProductProfit(a);
        var t = ProfitTotals(rows);
        var table = new ReportTable
        {
            Title = "Product Profitability",
            Meta = [("From", Date(a, start)), ("To", Date(a, end)), ("Cost", "Weighted average purchase cost")],
            Columns = [new("SKU"), new("Item", Width: 2.2), new("Unit", Width: 0.6), new("Qty Sold", true), new("Sales (excl. VAT)", true),
                new("Avg Cost", true), new("Cost of Sales", true), new("Gross Profit", true), new("Margin %", true)],
            Totals = ["", "TOTAL", "", "", F(t.Sales), "", F(t.Cost), F(t.Profit), t.Margin is { } mt ? $"{mt:0.00}" : ""],
        };
        foreach (var r in rows)
            table.Add(r.SkuCode, r.Name, r.Unit, Format.Qty(r.QuantitySold), F(r.Sales), F(r.AverageCost), F(r.CostOfSales), F(r.Profit),
                r.MarginPercent is { } mp ? $"{mp:0.00}" : "");
        return table;
    }

    public static ReportTable StockTable(Api a)
    {
        var filter = InventoryApi.RegisterFilter(a);
        var (rows, summary) = a.Read(tx =>
        {
            var all = new List<StockRegisterRow>();
            for (var n = 1; ; n++)
            {
                var page = StockQueries.Register(tx, filter, new PageRequest(n, 100));
                all.AddRange(page.Results);
                if (n >= page.TotalPages)
                    return (all, StockQueries.Summary(tx, filter));
            }
        });
        var table = new ReportTable
        {
            Title = "Stock Report",
            Meta = [.. filter.Start is { } s ? new[] { ("Movements from", Date(a, s)) } : [], ("As of", Date(a, filter.AsOf ?? a.Db.Today()))],
            Columns = [new("SKU"), new("Item", Width: 2.2), new("Unit", Width: 0.6), new("Purchased", true), new("Sold", true), new("Stock", true),
                new("Cost Value", true), new("Retail Value", true), new("Reorder Lvl", true), new("Status")],
            Totals = ["", "TOTAL", "", "", "", "", F(summary.TotalCostValue), F(summary.TotalRetailValue), "", ""],
        };
        foreach (var r in rows)
            table.Add(r.SkuCode, r.Name, r.Unit, Format.Qty(r.TotalPurchased), Format.Qty(r.TotalSold), Format.Qty(r.CurrentStock), F(r.CostValue),
                F(r.RetailValue), Format.Qty(r.ReorderLevel), Format.Status(r.StockStatus));
        return table;
    }

    public static ReportTable StockLedgerTable(Api a)
    {
        var filter = InventoryApi.LedgerFilter(a);
        var rows = a.Read(tx =>
        {
            var all = new List<StockLedgerRow>();
            for (var n = 1; ; n++)
            {
                var page = StockQueries.Ledger(tx, filter, new PageRequest(n, 100));
                all.AddRange(page.Results);
                if (n >= page.TotalPages || n >= 200)
                    return all;
            }
        });
        var table = new ReportTable
        {
            Title = "Stock Ledger", Meta = Period(a, filter.Start, filter.End),
            Columns = [new("Date"), new("Reference"), new("Type"), new("Product", Width: 2), new("Opening Qty", true), new("Qty In", true),
                new("Qty Out", true), new("Closing Qty", true)],
        };
        foreach (var r in rows)
            table.Add(Date(a, r.Date), r.ReferenceNumber, r.TransactionTypeDisplay, r.ProductName, Format.Qty(r.OpeningQuantity),
                Format.Qty(r.QuantityIn), Format.Qty(r.QuantityOut), Format.Qty(r.ClosingQuantity));
        return table;
    }

    public static ReportTable AccountBookTable(Api a, AccountBook book)
    {
        var table = new ReportTable
        {
            Title = $"{AccountKind.Display(book.Account.Kind)} Book",
            Meta = [("Account", book.Account.Name), ("From", book.Start is { } s ? Date(a, s) : "Beginning"), ("To", Date(a, book.End))],
            Columns = [new("Date", Width: 0.8), new("Type", Width: 0.7), new("Voucher", Width: 0.9), new("Particulars", Width: 2.4), new("Ref.", Width: 0.8),
                new("In", true), new("Out", true), new("Balance", true)],
            Totals = ["", "", "", "Closing Balance", "", F(book.TotalIn), F(book.TotalOut), F(book.ClosingBalance)],
        };
        table.Add("", "", "", "Opening Balance", "", "", "", F(book.OpeningBalance));
        foreach (var e in book.Entries)
            table.Add(Date(a, e.Date), e.VoucherType, e.Number, e.Particulars, e.ReferenceNumber, e.MoneyIn == 0 ? "" : F(e.MoneyIn),
                e.MoneyOut == 0 ? "" : F(e.MoneyOut), F(e.Balance));
        return table;
    }

    public static ReportTable DayBookTable(Api a, DayBook book)
    {
        var table = new ReportTable
        {
            Title = "Day Book", Meta = Period(a, book.Start, book.End),
            Summary = book.Totals.Select(t => (t.VoucherType, F(t.Amount))).ToList(),
            Columns = [new("Date", Width: 0.8), new("Type", Width: 0.7), new("Voucher", Width: 0.9), new("Particulars", Width: 2.4), new("Cash/Bank", Width: 1.2),
                new("Amount", true), new("Status", Width: 0.7)],
        };
        foreach (var e in book.Entries)
            table.Add(Date(a, e.Date), e.VoucherType, e.Number, e.Particulars, e.AccountName, F(e.Amount), Format.Status(e.Status));
        return table;
    }

    public static ReportTable LedgerBookTable(Api a, LedgerBook book)
    {
        static string DrCr(DrCr v) => $"{Format.Money(v.Amount)} {v.Side}";
        var table = new ReportTable
        {
            Title = $"Ledger: {book.Ledger.Name}",
            Meta = [("Group", book.Ledger.GroupLabel), ("From", book.Start is { } s ? Date(a, s) : "Beginning"), ("To", Date(a, book.End))],
            Columns = [new("Date", Width: 0.8), new("Voucher", Width: 0.9), new("Particulars", Width: 2.6), new("Debit", true), new("Credit", true), new("Balance", Width: 1.1)],
            Totals = ["", "", "Closing Balance", F(book.TotalDebit), F(book.TotalCredit), DrCr(book.Closing)],
        };
        table.Add("", "", "Opening Balance", "", "", DrCr(book.Opening));
        foreach (var e in book.Entries)
            table.Add(Date(a, e.Date), e.Number, e.Narration, e.Debit == 0 ? "" : F(e.Debit), e.Credit == 0 ? "" : F(e.Credit), DrCr(e.Balance));
        return table;
    }

    /// <summary>Statement rows (label, amount, total) like the Django statement exports.</summary>
    private static ReportTable Statement(string title, IEnumerable<(string, string)> meta, IEnumerable<(string Label, decimal? Amount, decimal? Total)> rows,
        IEnumerable<(string, string)>? summary = null)
    {
        var table = new ReportTable
        {
            Title = title, Meta = meta.ToList(), Summary = summary?.ToList() ?? [],
            Columns = [new("Particulars", Width: 3), new("Amount", true), new("Total", true)],
        };
        foreach (var (label, amount, total) in rows)
        {
            string[] cells = [label, amount is { } x ? F(x) : "", total is { } t ? F(t) : ""];
            if (total is not null || amount is null)
                table.AddBold(cells);
            else
                table.Add(cells);
        }
        return table;
    }

    private const string Indent = "    ";

    private static IEnumerable<(string, decimal?, decimal?)> Items(IEnumerable<StatementRow> rows, int sign = 1) =>
        rows.Select(r => (Indent + r.Name, (decimal?)(sign * r.Amount), (decimal?)null));

    private static object Rows(IEnumerable<StatementRow> rows) => rows.Select(r => new { id = r.Id, name = r.Name, amount = Json.M(r.Amount), link = r.Link }).ToList();

    private static object Groups(IEnumerable<StatementGroup> groups) =>
        groups.Select(g => new { key = g.Key, label = g.Label, rows = Rows(g.Rows), total = Json.M(g.Total) }).ToList();

    private static (DateOnly? Start, DateOnly End) StatementPeriod(Api a)
    {
        var end = a.Date("end_date") ?? a.Db.Today();
        var start = a.Date("start_date");
        if (start > end)
            throw new BusinessError("Start date must be on or before the end date.", "INVALID_PERIOD");
        return (start, end);
    }

    // ------------------------------------------------------------------ endpoints

    public static void Map(RouteGroupBuilder api)
    {
        // Sales & purchase reports
        foreach (var kind in new[] { TradeKind.Sale, TradeKind.Purchase })
            api.MapGet($"reports/{kind.Table}", (HttpContext h) =>
            {
                var a = Api.Of(h);
                a.Require("reports.view");
                var filter = TradeApi.Filter(a, kind) with { Status = a.Q("status") };
                var summary = a.Read(tx => BusinessReports.TradeSummary(tx, kind, filter));
                if (a.Q("export") is { } exportKind)
                {
                    var rows = a.Read(tx => BusinessReports.AllTradeRows(tx, kind, filter));
                    var table = new ReportTable
                    {
                        Title = kind.IsSale ? "Sales Report" : "Purchase Report", Meta = Period(a, filter.Start, filter.End),
                        Summary = [("Documents", summary.TotalDocuments.ToString()), ("Gross", F(summary.GrossAmount)), ("Discount", F(summary.TotalDiscount)),
                            ("Taxable", F(summary.TaxableAmount)), ("Tax", F(summary.TaxAmount)), ("Total", F(summary.TotalAmount)), ("Outstanding", F(summary.Outstanding))],
                        Columns = [new("Date"), new(kind.IsSale ? "Invoice #" : "Bill #"), new(kind.PartyLabel, Width: 2), new("Items", Width: 0.6), new("Quantity", true),
                            new("Gross", true), new("Discount", true), new("Tax", true), new("Total", true), new("Balance", true), new("Payment Status")],
                        Totals = ["", "", "TOTAL", "", "", F(summary.GrossAmount), F(summary.TotalDiscount), F(summary.TaxAmount), F(summary.TotalAmount), F(summary.Outstanding), ""],
                    };
                    foreach (var r in rows)
                        table.Add(Date(a, r.Document.Date), r.Document.Number, r.Document.PartyName, r.Items.ToString(), Format.Qty(r.Quantity),
                            F(r.Document.Subtotal), F(r.Document.TotalDiscount), F(r.Document.TaxAmount), F(r.Document.TotalAmount),
                            F(r.Document.BalanceDue), Format.Status(r.Document.PaymentStatus));
                    return Export(a, exportKind, table);
                }
                var page = a.Read(tx => BusinessReports.TradeRows(tx, kind, filter, a.Page));
                return Api.Paged(page, r => new
                {
                    id = r.Document.Id, date = Json.D(r.Document.Date), number = r.Document.Number, party_id = r.Document.PartyId, party_name = r.Document.PartyName,
                    items = r.Items, quantity = Json.Q(r.Quantity), gross_amount = Json.M(r.Document.Subtotal), discount = Json.M(r.Document.TotalDiscount),
                    taxable_amount = Json.M(r.Document.TaxableAmount), tax_amount = Json.M(r.Document.TaxAmount), total_amount = Json.M(r.Document.TotalAmount),
                    amount_paid = Json.M(r.Document.AmountPaid), balance_due = Json.M(r.Document.BalanceDue), due_date = Json.D(r.Document.DueDate),
                    payment_status = r.Document.PaymentStatus, status = r.Document.Status,
                }, new Dictionary<string, object?>
                {
                    ["summary"] = new
                    {
                        total_documents = summary.TotalDocuments, gross_amount = Json.M(summary.GrossAmount), item_discount = Json.M(summary.ItemDiscount),
                        invoice_discount = Json.M(summary.InvoiceDiscount), total_discount = Json.M(summary.TotalDiscount), taxable_amount = Json.M(summary.TaxableAmount),
                        tax_amount = Json.M(summary.TaxAmount), net_amount = Json.M(summary.TaxableAmount), total_amount = Json.M(summary.TotalAmount),
                        amount_paid = Json.M(summary.AmountPaid), outstanding = Json.M(summary.Outstanding),
                    },
                });
            });

        // Expense report
        api.MapGet("reports/expenses", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("reports.view");
            var filter = ExpensesApi.Filter(a) with { Status = a.Q("status") };
            var summary = a.Read(tx => BusinessReports.ExpenseSummary(tx, filter));
            if (a.Q("export") is { } exportKind)
            {
                var rows = a.Read(tx => ExpenseService.All(tx, filter));
                var table = new ReportTable
                {
                    Title = "Expense Report", Meta = Period(a, filter.Start, filter.End),
                    Summary = [("Expenses", summary.TotalExpenses.ToString()), ("Amount", F(summary.Amount)), ("Tax", F(summary.TaxAmount)), ("Total", F(summary.TotalAmount)),
                        .. summary.JournalAmount != 0 ? new[] { ("Journal entries", F(summary.JournalAmount)) } : [],
                        .. summary.ByCategory.Take(4).Select(c => (c.CategoryName, F(c.TotalWithJournal)))],
                    Columns = [new("Date"), new("Expense #"), new("Category", Width: 1.4), new("Description", Width: 2.2), new("Paid To", Width: 1.5),
                        new("Paid From"), new("Amount", true), new("Tax", true), new("Total", true), new("Status", Width: 0.8)],
                    Totals = ["", "", "", "TOTAL", "", "", F(summary.Amount), F(summary.TaxAmount), F(summary.TotalAmount), ""],
                };
                foreach (var e in rows.OrderByDescending(e => e.Date).ThenByDescending(e => e.Id))
                    table.Add(Date(a, e.Date), e.Number, e.CategoryName, e.Description, e.PaidTo, e.AccountName, F(e.Amount), F(e.TaxAmount), F(e.TotalAmount), Format.Status(e.Status));
                return Export(a, exportKind, table);
            }
            var (page, rows2) = a.Read(tx =>
            {
                var p = ExpenseService.List(tx, filter with { Status = filter.Status ?? DocumentStatus.Active }, a.Page);
                return (p, p.Results.Select(e => new
                {
                    id = e.Id, date = Json.D(e.Date), number = e.Number, category_id = e.CategoryId, category_name = e.CategoryName, description = e.Description,
                    paid_to = e.PaidTo, payment_method = PaymentMethod.Display(e.PaymentMethod), account = e.AccountId, account_name = e.AccountName,
                    amount = Json.M(e.Amount), tax_amount = Json.M(e.TaxAmount), total_amount = Json.M(e.TotalAmount), status = e.Status,
                }).Cast<object>().ToList());
            });
            return Api.Paged(new Page<object>(rows2, page.Count, page.PageNumber, page.PageSize), x => x, new Dictionary<string, object?>
            {
                ["summary"] = new
                {
                    total_expenses = summary.TotalExpenses, amount = Json.M(summary.Amount), tax_amount = Json.M(summary.TaxAmount), total_amount = Json.M(summary.TotalAmount),
                    journal_amount = Json.M(summary.JournalAmount), total_with_journal = Json.M(summary.TotalWithJournal),
                    by_category = summary.ByCategory.Select(c => new
                    {
                        category_id = c.CategoryId, category_name = c.CategoryName, count = c.Count, amount = Json.M(c.Amount), tax_amount = Json.M(c.TaxAmount),
                        total_amount = Json.M(c.TotalAmount), journal_amount = Json.M(c.JournalAmount), total_with_journal = Json.M(c.TotalWithJournal),
                    }).ToList(),
                },
            });
        });

        api.MapGet("reports/product-profit", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("reports.view");
            if (a.Q("export") is { } kind)
                return Export(a, kind, ProductProfitTable(a));
            var (rows, start, end) = ProductProfit(a);
            var t = ProfitTotals(rows);
            var page = a.Page;
            var results = rows.Skip(page.Offset).Take(page.Size).ToList();
            return Api.Paged(new Page<ProductProfitRow>(results, rows.Count, page.Page, page.Size), r => new
            {
                id = r.ProductId, sku_code = r.SkuCode, name = r.Name, unit = r.Unit, quantity = Json.Q(r.QuantitySold), sales = Json.M(r.Sales),
                average_cost = Json.M(r.AverageCost), cost = Json.M(r.CostOfSales), profit = Json.M(r.Profit),
                margin = r.MarginPercent is { } mp ? Json.R(mp) : null,
            }, new Dictionary<string, object?>
            {
                ["summary"] = new
                {
                    products = rows.Count, sales = Json.M(t.Sales), cost = Json.M(t.Cost), profit = Json.M(t.Profit),
                    margin = t.Margin is { } mt ? Json.R(mt) : null, loss_making = rows.Count(r => r.Profit < 0),
                    start_date = Json.D(start), end_date = Json.D(end),
                },
            });
        });

        // Stock reports
        api.MapGet("reports/stock", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("inventory.view");
            if (a.Q("export") is { } kind)
                return Export(a, kind, StockTable(a));
            var filter = InventoryApi.RegisterFilter(a);
            var (page, summary, openings) = a.Read(tx => (StockQueries.Register(tx, filter, a.Page), StockQueries.Summary(tx, filter),
                tx.Rows("SELECT id, opening_stock FROM products").ToDictionary(r => r.Long("id"), r => r.Qty("opening_stock"))));
            return Api.Paged(page, r => new
            {
                id = r.ProductId, sku_code = r.SkuCode, name = r.Name, unit = r.Unit, opening_stock = Json.Q(openings.GetValueOrDefault(r.ProductId)),
                total_purchased = Json.Q(r.TotalPurchased), total_sold = Json.Q(r.TotalSold), total_adjusted = Json.Q(r.TotalAdjusted),
                current_stock = Json.Q(r.CurrentStock), purchase_price = Json.M(r.PurchasePrice), selling_price = Json.M(r.SellingPrice),
                cost_value = Json.M(r.CostValue), retail_value = Json.M(r.RetailValue), reorder_level = Json.Q(r.ReorderLevel), stock_status = r.StockStatus,
                is_active = r.IsActive,
            }, new Dictionary<string, object?>
            {
                ["summary"] = new
                {
                    total_products = summary.ProductCount, total_cost_value = Json.M(summary.TotalCostValue), total_retail_value = Json.M(summary.TotalRetailValue),
                    low_stock_count = summary.LowStockCount, out_of_stock_count = summary.OutOfStockCount, as_of = Json.D(filter.AsOf), start_date = Json.D(filter.Start),
                },
            });
        });
        api.MapGet("reports/stock-ledger", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("inventory.view");
            if (a.Q("export") is { } kind)
                return Export(a, kind, StockLedgerTable(a));
            return Api.Paged(a.Read(tx => StockQueries.Ledger(tx, InventoryApi.LedgerFilter(a), a.Page)), InventoryApi.LedgerJson);
        });

        // Receivables / payables: outstanding documents and aging
        foreach (var (route, cfg) in new[] { ("receivables", AccountConfig.Receivable), ("payables", AccountConfig.Payable) })
        {
            api.MapGet($"reports/{route}", (HttpContext h) =>
            {
                var a = Api.Of(h);
                a.Require("reports.view");
                var asOf = a.Date("as_of") ?? a.Db.Today();
                var report = a.Read(tx => BusinessReports.Outstanding(tx, cfg, asOf, a.Int("party"), a.Q("overdue") == "true"));
                if (a.Q("export") is { } kind)
                {
                    var table = new ReportTable
                    {
                        Title = cfg.Documents.IsSale ? "Receivables Report" : "Payables Report", Meta = [("As of", Date(a, asOf))],
                        Columns = [new("Number"), new("Date"), new("Due Date"), new(cfg.Documents.PartyLabel, Width: 2), new("Total", true), new("Paid", true),
                            new("Outstanding", true), new("Days Overdue")],
                        Totals = ["", "", "", "TOTAL", F(report.Total), "", F(report.Outstanding), ""],
                    };
                    foreach (var r in report.Rows)
                        table.Add(r.Document.Number, Date(a, r.Document.Date), Date(a, r.Document.DueDate), r.PartyName, F(r.Document.TotalAmount),
                            F(r.Document.PaidAsOf), F(r.Document.Outstanding), r.Document.DaysOverdue.ToString());
                    return Export(a, kind, table);
                }
                return a.PagedList(report.Rows, r => new
                {
                    id = r.Document.Id, number = r.Document.Number, date = Json.D(r.Document.Date), due_date = Json.D(r.Document.DueDate), party_id = r.PartyId,
                    party_name = r.PartyName, total_amount = Json.M(r.Document.TotalAmount), paid = Json.M(r.Document.PaidAsOf), outstanding = Json.M(r.Document.Outstanding),
                    days_overdue = r.Document.DaysOverdue, bucket = r.Document.Bucket,
                    payment_status = r.Document.PaidAsOf == 0 ? PaymentStatus.Unpaid : PaymentStatus.Partial,
                }, new Dictionary<string, object?>
                {
                    ["summary"] = new { count = report.Rows.Count, total = Json.M(report.Total), outstanding = Json.M(report.Outstanding), overdue = Json.M(report.Overdue) },
                    ["as_of"] = Json.D(asOf),
                });
            });

            api.MapGet($"reports/{route}-aging", (HttpContext h) =>
            {
                var a = Api.Of(h);
                a.Require("reports.view");
                var asOf = a.Date("as_of") ?? a.Db.Today();
                var aging = a.Read(tx => PartyLedgers.Aging(tx, cfg, asOf, a.Int("party"), a.Q("search")));
                if (a.Q("export") is { } kind)
                {
                    var table = new ReportTable
                    {
                        Title = cfg.Documents.IsSale ? "Accounts Receivable Aging" : "Accounts Payable Aging", Meta = [("As of", Date(a, asOf))],
                        Columns = [new(cfg.Documents.PartyLabel, Width: 2.2), new("Current", true), new("1-30", true), new("31-60", true), new("61-90", true),
                            new("91-120", true), new("120+", true), new("Total", true), new("Advances", true), new("Net Balance", true)],
                        Totals = ["TOTAL", .. AgingBucket.All.Select(b => F(aging.Totals[b.Key])), F(aging.Totals.Total), F(aging.Totals.Unallocated), F(aging.Totals.NetBalance)],
                    };
                    foreach (var r in aging.Rows)
                        table.Add([r.PartyName, .. AgingBucket.All.Select(b => F(r.Amounts[b.Key])), F(r.Amounts.Total), F(r.Amounts.Unallocated), F(r.Amounts.NetBalance)]);
                    return Export(a, kind, table);
                }
                return a.PagedList(aging.Rows, r =>
                {
                    var row = PaymentsApi.AmountsJson(r.Amounts);
                    row["party_id"] = r.PartyId;
                    row["party_name"] = r.PartyName;
                    row["phone"] = r.Phone;
                    row["pan_vat_no"] = r.PanVatNo;
                    row["credit_limit"] = Json.M(r.CreditLimit);
                    row["document_count"] = r.DocumentCount;
                    return row;
                }, new Dictionary<string, object?> { ["as_of"] = Json.D(asOf), ["buckets"] = PaymentsApi.Buckets, ["totals"] = PaymentsApi.AmountsJson(aging.Totals) });
            });
        }

        // Financial statements
        api.MapGet("reports/profit-loss", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("reports.view");
            var (start, end) = StatementPeriod(a);
            var d = a.Read(tx => Statements.ProfitAndLoss(tx, start, end));
            if (a.Q("export") is { } kind)
                return Export(a, kind, Statement("Profit & Loss Account", [("From", start is { } s ? Date(a, s) : "Beginning"), ("To", Date(a, end))],
                [
                    ("Sales (excluding VAT)", null, d.Sales),
                    .. d.DirectIncome.Count > 0 ? [("Direct income", null, null), .. Items(d.DirectIncome)] : Array.Empty<(string, decimal?, decimal?)>(),
                    ("Less: Cost of goods sold", null, null),
                    (Indent + "Opening stock", d.OpeningStock, null),
                    .. d.OpeningStockAdded != 0 ? new (string, decimal?, decimal?)[] { (Indent + "Opening stock added", d.OpeningStockAdded, null) } : [],
                    (Indent + "Purchases (excluding VAT)", d.Purchases, null),
                    (Indent + "Less: Closing stock", -d.ClosingStock, null),
                    ("Cost of goods sold", null, -d.CostOfGoodsSold),
                    .. d.DirectExpenses.Count > 0 ? [("Direct expenses", null, null), .. Items(d.DirectExpenses, -1)] : Array.Empty<(string, decimal?, decimal?)>(),
                    ("GROSS PROFIT", null, d.GrossProfit),
                    .. d.IndirectIncome.Count > 0 ? [("Add: Indirect income", null, null), .. Items(d.IndirectIncome)] : Array.Empty<(string, decimal?, decimal?)>(),
                    ("Less: Expenses", null, null),
                    .. Items(d.ExpenseCategories, -1),
                    .. Items(d.IndirectExpenses, -1),
                    ("Total expenses", null, -d.TotalIndirectExpenses),
                    (d.NetProfit >= 0 ? "NET PROFIT" : "NET LOSS", null, d.NetProfit),
                ], [("Gross profit", F(d.GrossProfit)), ("Net profit", F(d.NetProfit))]));
            return Api.Ok(new
            {
                start_date = Json.D(d.Start), end_date = Json.D(d.End), sales = Json.M(d.Sales), direct_income = Rows(d.DirectIncome), opening_stock = Json.M(d.OpeningStock),
                opening_stock_added = Json.M(d.OpeningStockAdded), purchases = Json.M(d.Purchases), closing_stock = Json.M(d.ClosingStock),
                cost_of_goods_sold = Json.M(d.CostOfGoodsSold), direct_expenses = Rows(d.DirectExpenses), gross_profit = Json.M(d.GrossProfit),
                indirect_income = Rows(d.IndirectIncome), expense_categories = Rows(d.ExpenseCategories), indirect_expenses = Rows(d.IndirectExpenses),
                total_indirect_expenses = Json.M(d.TotalIndirectExpenses), net_profit = Json.M(d.NetProfit),
            });
        });

        api.MapGet("reports/balance-sheet", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("reports.view");
            var asOf = a.Date("as_of") ?? a.Db.Today();
            var d = a.Read(tx => Statements.BalanceSheet(tx, asOf));
            if (a.Q("export") is { } kind)
            {
                var rows = new List<(string, decimal?, decimal?)> { ("CAPITAL & LIABILITIES", null, null) };
                foreach (var g in d.Liabilities)
                    rows.AddRange([(g.Label, null, g.Total), .. Items(g.Rows)]);
                rows.AddRange([("Total capital & liabilities", null, d.TotalLiabilities), ("", null, null), ("ASSETS", null, null)]);
                foreach (var g in d.Assets)
                    rows.AddRange([(g.Label, null, g.Total), .. Items(g.Rows)]);
                rows.Add(("Total assets", null, d.TotalAssets));
                return Export(a, kind, Statement("Balance Sheet", [("As at", Date(a, asOf))], rows));
            }
            return Api.Ok(new
            {
                as_of = Json.D(d.AsOf), liabilities = Groups(d.Liabilities), assets = Groups(d.Assets), total_liabilities = Json.M(d.TotalLiabilities),
                total_assets = Json.M(d.TotalAssets), difference = Json.M(d.Difference), net_profit = Json.M(d.NetProfit),
            });
        });

        api.MapGet("reports/cash-flow", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("reports.view");
            var (start, end) = StatementPeriod(a);
            if (start is not { } s)
                throw new BusinessError("Choose a start date for the cash flow.", "START_REQUIRED");
            var d = a.Read(tx => Statements.CashFlow(tx, s, end));
            if (a.Q("export") is { } kind)
            {
                var rows = new List<(string, decimal?, decimal?)> { ("Opening cash & bank balance", null, d.OpeningCash) };
                foreach (var (label, items, net) in new[] { ("Operating activities", d.Operating, d.NetOperating), ("Investing activities", d.Investing, d.NetInvesting),
                             ("Financing activities", d.Financing, d.NetFinancing) })
                    rows.AddRange([(label, null, null), .. Items(items), ($"Net cash from {label.ToLowerInvariant()}", null, net)]);
                rows.AddRange([("Net increase / (decrease) in cash", null, d.NetChange), ("Closing cash & bank balance", null, d.ClosingCash)]);
                return Export(a, kind, Statement("Cash Flow Statement", [("From", Date(a, s)), ("To", Date(a, end))], rows));
            }
            return Api.Ok(new
            {
                start_date = Json.D(d.Start), end_date = Json.D(d.End), opening_cash = Json.M(d.OpeningCash), operating = Rows(d.Operating), investing = Rows(d.Investing),
                financing = Rows(d.Financing), net_operating = Json.M(d.NetOperating), net_investing = Json.M(d.NetInvesting), net_financing = Json.M(d.NetFinancing),
                net_change = Json.M(d.NetChange), closing_cash = Json.M(d.ClosingCash), difference = Json.M(d.Difference), accounts = Rows(d.Accounts),
            });
        });

        api.MapGet("reports/trial-balance", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("reports.view");
            var (start, end) = StatementPeriod(a);
            var d = a.Read(tx => Statements.TrialBalance(tx, start, end));
            if (a.Q("export") is { } kind)
            {
                var table = new ReportTable
                {
                    Title = "Trial Balance", Meta = [("From", start is { } s ? Date(a, s) : "Beginning"), ("To", Date(a, end))],
                    Columns = [new("Particulars", Width: 3), new("Debit", true), new("Credit", true)],
                };
                foreach (var g in d.Groups)
                {
                    table.AddBold(g.Label, g.Debit == 0 ? "" : F(g.Debit), g.Credit == 0 ? "" : F(g.Credit));
                    if (a.Q("detailed") is not null)
                        foreach (var r in g.Rows)
                            table.Add(Indent + r.Name, r.Debit == 0 ? "" : F(r.Debit), r.Credit == 0 ? "" : F(r.Credit));
                }
                table.AddBold("Total", F(d.TotalDebit), F(d.TotalCredit));
                return Export(a, kind, table);
            }
            return Api.Ok(new
            {
                start_date = Json.D(d.Start), end_date = Json.D(d.End), total_debit = Json.M(d.TotalDebit), total_credit = Json.M(d.TotalCredit),
                difference = Json.M(d.Difference),
                groups = d.Groups.Select(g => new
                {
                    key = g.Key, label = g.Label, debit = Json.M(g.Debit), credit = Json.M(g.Credit), count = g.Rows.Count,
                    rows = g.Rows.Select(r => new { id = r.Id, name = r.Name, debit = Json.M(r.Debit), credit = Json.M(r.Credit), link = r.Link }).ToList(),
                }).ToList(),
            });
        });

        // Opening balances
        object Openings(Api a)
        {
            var d = a.Read(OpeningBalances.Get);
            object Dc(DebitCredit x) => new { debit = Json.M(x.Debit), credit = Json.M(x.Credit) };
            object Row(OpeningRow r, string section) => section switch
            {
                "cash" => new { id = r.Id, name = r.Name, kind = r.Detail, is_active = r.IsActive, opening_balance = Json.M(r.OpeningBalance), opening_side = r.OpeningSide },
                "ledgers" => new { id = r.Id, name = r.Name, group = (string?)null, group_display = r.Detail, is_active = r.IsActive, opening_balance = Json.M(r.OpeningBalance), opening_side = r.OpeningSide },
                _ => new { id = r.Id, name = r.Name, phone = r.Detail, is_active = r.IsActive, opening_balance = Json.M(r.OpeningBalance), opening_side = r.OpeningSide },
            };
            var groupCodes = a.Read(tx => JournalService.Ledgers(tx).ToDictionary(l => l.Id, l => l.Group));
            return new
            {
                cash = d.Cash.Select(r => Row(r, "cash")).ToList(),
                ledgers = d.Ledgers.Select(r => new
                {
                    id = r.Id, name = r.Name, group = groupCodes.GetValueOrDefault(r.Id), group_display = r.Detail, is_active = r.IsActive,
                    opening_balance = Json.M(r.OpeningBalance), opening_side = r.OpeningSide,
                }).ToList(),
                customers = d.Customers.Select(r => Row(r, "customers")).ToList(),
                vendors = d.Vendors.Select(r => Row(r, "vendors")).ToList(),
                stock = d.Stock.Select(r => new
                {
                    id = r.Id, name = r.Name, sku_code = r.SkuCode, unit = r.Unit, is_active = r.IsActive, opening_stock = Json.Q(r.OpeningStock),
                    opening_rate = Json.M(r.OpeningRate), opening_amount = Json.M(r.OpeningAmount),
                }).ToList(),
                totals = d.Totals.ToDictionary(t => t.Key, t => Dc(t.Value)),
                total_debit = Json.M(d.TotalDebit), total_credit = Json.M(d.TotalCredit), difference = Dc(d.Difference),
                can_edit = OpeningBalances.SectionPermissions.ToDictionary(p => p.Key, p => a.Can(p.Value)),
            };
        }
        api.MapGet("reports/opening-balances", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("reports.view");
            return Api.Ok(Openings(a));
        });
        api.MapPost("reports/opening-balances", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("reports.view");
            var b = await a.BodyAsync();
            List<OpeningChange> Changes(string section, Func<long, (decimal, string)> current) => b.List(section).Select(x =>
            {
                var id = x.ReqLong("id");
                var (amount, side) = current(id);
                return new OpeningChange(id, x.Has("opening_balance") ? x.Dec("opening_balance") ?? 0 : amount, x.Str("opening_side") ?? side);
            }).ToList();
            var changes = a.Read(tx => new OpeningChanges
            {
                Cash = Changes("cash", id => (MoneyAccounts.Get(tx, id).OpeningBalance, "DR")),
                Ledgers = Changes("ledgers", id => JournalService.GetLedger(tx, id) is var l ? (l.OpeningBalance, l.OpeningSide) : default),
                Customers = Changes("customers", id => PartyService.Get(tx, id) is var p ? (p.OpeningBalance, p.OpeningSide) : default),
                Vendors = Changes("vendors", id => PartyService.Get(tx, id) is var p ? (p.OpeningBalance, p.OpeningSide) : default),
                Stock = b.List("stock").Select(x =>
                {
                    var p = ProductService.Get(tx, x.ReqLong("id"));
                    return new StockOpeningChange(p.Id, x.Has("opening_stock") ? x.Dec("opening_stock") ?? 0 : p.OpeningStock,
                        x.Has("opening_rate") ? x.Dec("opening_rate") ?? 0 : p.OpeningRate);
                }).ToList(),
            });
            var count = a.Write((tx, actor) => OpeningBalances.Save(tx, actor, changes));
            return Api.Ok(Openings(a), count > 0 ? $"{count} opening balance(s) saved." : "No changes to save.");
        });

        // Dashboard and global search
        api.MapGet("dashboard", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("dashboard.view");
            var d = a.Read(tx => BusinessReports.Dashboard(tx, a.Can("reports.view"), a.Can("expenses.view"), a.Date("start_date"), a.Date("end_date")));
            var cards = d.Cards.ToDictionary(c => c.Key, c => (object?)Json.M(c.Value));
            cards["today"] = Json.D(a.Db.Today());
            cards["low_stock_count"] = d.LowStockCount;
            cards["total_customers"] = d.TotalCustomers;
            cards["total_vendors"] = d.TotalVendors;
            return Api.Ok(new
            {
                start_date = Json.D(d.Start), end_date = Json.D(d.End), granularity = d.Monthly ? "month" : "day", cards,
                trend = d.Trend.Select(t => new
                {
                    date = Json.D(t.Date), sales = Json.M(t.Sales), purchases = Json.M(t.Purchases), receipts = Json.M(t.Receipts), payments = Json.M(t.Payments),
                    expenses = Json.M(t.Expenses),
                }).ToList(),
                top_products = d.TopProducts.Select(p => new { product_id = p.ProductId, name = p.Name, sku_code = p.SkuCode, quantity = Json.Q(p.Quantity), amount = Json.M(p.Amount) }).ToList(),
                stock_value_by_product = d.StockValueByProduct.Select(p => new { product_id = p.ProductId, name = p.Name, sku_code = p.SkuCode, value = Json.M(p.CostValue), quantity = Json.Q(p.CurrentStock) }).ToList(),
                low_stock = d.LowStock.Select(p => new
                {
                    id = p.ProductId, name = p.Name, sku_code = p.SkuCode, current_stock = Json.Q(p.CurrentStock), reorder_level = Json.Q(p.ReorderLevel),
                    unit = p.Unit, stock_status = p.StockStatus,
                }).ToList(),
                recent_sales = d.RecentSales.Select(s => new
                {
                    id = s.Id, number = s.Number, date = Json.D(s.Date), party_name = s.PartyName, total_amount = Json.M(s.TotalAmount), payment_status = s.PaymentStatus,
                }).ToList(),
            });
        });

        api.MapGet("search", (HttpContext h) => SearchApi.Search(Api.Of(h)));
    }
}
