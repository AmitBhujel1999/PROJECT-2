using Accounting.Core;
using System.Text;
using Accounting.Core.Common;
using Accounting.Core.Expenses;
using Accounting.Core.Journal;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Reports;
using Accounting.Core.Trade;
using static Accounting.Tests.TestDb;

namespace Accounting.Tests;

// The export parts of backend/tests/test_reports.py: every document and report renders, CSV is safe.
public class ExportTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    private static bool IsPdf(byte[] bytes) => bytes.Length > 1000 && Encoding.ASCII.GetString(bytes, 0, 4) == "%PDF";

    [Fact]
    public void EveryDocumentRendersAsPdf()
    {
        var p = _t.Product(price: 100);
        var sale = _t.MakeSale(_t.Customer(), [Item(p, 2, null, DiscountType.Percentage, 5)], payment: new PaymentInput { Amount = 100 }).Document;
        var bill = _t.MakePurchase(_t.Vendor(), [Item(p, 3, 40)]);
        var receiptId = _t.Read(tx => PartyAccounts.ListPayments(tx, AccountConfig.Receivable, new PaymentFilter())).Results[0].Id;
        var rent = _t.Read(tx => ExpenseService.Categories(tx)).First();
        var expense = _t.Write(tx => ExpenseService.Create(tx, _t.Admin, new ExpenseInput { Date = TestDb.Today, CategoryId = rent.Id, Description = "Tea", Amount = 50, TaxRate = 13 }));
        var voucher = _t.Write(tx =>
        {
            var capital = JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Capital", "CAPITAL"));
            var furniture = JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Furniture", "FIXED_ASSETS"));
            return JournalService.CreateVoucher(tx, _t.Admin, TestDb.Today, "Owner's furniture", [new($"ledger:{furniture.Id}", 900), new($"ledger:{capital.Id}", Credit: 900)]);
        });
        _t.Read(tx =>
        {
            Assert.True(IsPdf(DocumentPdf.TradeDocument(tx, TradeKind.Sale, sale.Id)));
            Assert.True(IsPdf(DocumentPdf.TradeDocument(tx, TradeKind.Purchase, bill.Id)));
            Assert.True(IsPdf(DocumentPdf.Payment(tx, AccountConfig.Receivable, receiptId)));
            Assert.True(IsPdf(DocumentPdf.Expense(tx, expense.Id)));
            Assert.True(IsPdf(DocumentPdf.Journal(tx, voucher.Id)));
            var settings = BusinessSettings.Get(tx);
            Assert.True(IsPdf(Pdf.Write(DocumentPdf.Statement(tx, AccountConfig.Receivable, _t.Customer().Id, null, null), settings)));
            return 0;
        });
    }

    [Fact]
    public void StatementCsvHasOpeningAndClosing()
    {
        var p = _t.Product(price: 100);
        var sale = _t.MakeSale(_t.Customer(), [Item(p, 1)], new DateOnly(2026, 9, 10)).Document;
        var text = _t.Read(tx => Encoding.UTF8.GetString(Csv.Write(DocumentPdf.Statement(tx, AccountConfig.Receivable, _t.Customer().Id, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), BusinessSettings.Get(tx))));
        Assert.StartsWith("﻿", text);
        Assert.Contains("Opening Balance", text);
        Assert.Contains("Closing Balance", text);
        Assert.Contains(sale.Number, text);
    }

    [Fact]
    public void CsvNeutralisesFormulaInjection()
    {
        Assert.Equal("'=HYPERLINK(\"http://evil\")", Csv.Safe("=HYPERLINK(\"http://evil\")"));
        Assert.Equal("'@SUM(A1)", Csv.Safe("@SUM(A1)"));
        Assert.Equal("-1,234.50", Csv.Safe("-1,234.50")); // a genuine negative number stays
        var table = new ReportTable { Title = "T", Columns = [new("Name"), new("Amount", true)] };
        table.Add("=cmd|' /C calc'!A0", "1,00,000.00");
        var text = Encoding.UTF8.GetString(Csv.Write(table, new BusinessSettings()));
        Assert.Contains("'=cmd|' /C calc'!A0", text);
        Assert.Contains("\"1,00,000.00\"", text); // commas are quoted
    }

    [Fact]
    public void ReportTablePdfWithManyRowsPaginates()
    {
        var table = new ReportTable { Title = "Long report", Columns = [new("#"), new("Name", Width: 3), new("Amount", true)], Summary = [("Rows", "300")] };
        for (var i = 1; i <= 300; i++)
            table.Add(i.ToString(), $"Row {i}", Format.Money(i * 1000.5m));
        table = new ReportTable { Title = table.Title, Columns = table.Columns, Rows = table.Rows, Summary = table.Summary, Totals = ["", "TOTAL", "1"] };
        var bytes = Pdf.Write(table, new BusinessSettings());
        Assert.True(IsPdf(bytes));
        Assert.Contains("/Count ", Encoding.ASCII.GetString(bytes));
        Assert.Equal("long-report-20260927.pdf", table.FileName("pdf", TestDb.Today));
    }
}
