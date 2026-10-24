using Accounting.Core;
using Accounting.Core.CashBank;
using Accounting.Core.Expenses;
using Accounting.Core.Journal;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Products;
using Accounting.Core.Reports;
using Accounting.Core.Trade;
using static Accounting.Tests.TestDb;

namespace Accounting.Tests;

// Ported from backend/tests/test_statements.py.
public class StatementTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly MoneyAccount _bank;
    private readonly Product _product;

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>A small set of books touching every kind of entry (the "books" fixture).</summary>
    public StatementTests()
    {
        _bank = _t.Write(tx => MoneyAccounts.Create(tx, _t.Admin, new MoneyAccount { Name = "Nabil Bank", Kind = AccountKind.Bank, OpeningBalance = 100000 }));
        _product = _t.Product(stock: 10, cost: 100, price: 200, tax: 13);
        var customer = _t.Customer();
        var vendor = _t.Vendor();
        // Purchase 10 @ 130 + 13% VAT (unpaid).
        _t.MakePurchase(vendor, [Item(_product, 10, 130)], D(2026, 9, 2));
        // Sale 5 @ 200 + 13% VAT, paid in full into the bank.
        _t.MakeSale(customer, [Item(_product, 5)], D(2026, 9, 5), new PaymentInput { PayInFull = true, AccountId = _bank.Id });
        // A sale that is later cancelled must not count anywhere.
        var cancelled = _t.MakeSale(customer, [Item(_product, 2)], D(2026, 9, 6)).Document;
        _t.Write(tx => TradeService.Cancel(tx, _t.Admin, TradeKind.Sale, cancelled.Id, "test"));
        // Pay the vendor 500, pay rent 1,000 (no VAT).
        _t.Write(tx => PartyAccounts.CreatePayment(tx, _t.Admin, AccountConfig.Payable, vendor.Id, D(2026, 9, 7), 500, accountId: _bank.Id));
        var rent = _t.Read(tx => ExpenseService.Categories(tx)).Single(c => c.Name == "Rent");
        _t.Write(tx => ExpenseService.Create(tx, _t.Admin, new ExpenseInput { Date = D(2026, 9, 8), CategoryId = rent.Id, Description = "Rent", Amount = 1000, AccountId = _bank.Id }));
        // Journals: furniture brought in by the owner; a bank loan received into the bank.
        _t.Write(tx =>
        {
            var furniture = JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Furniture T", "FIXED_ASSETS", OpeningSide: "DR"));
            var capital = JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Capital T", "CAPITAL", OpeningSide: "CR"));
            var loan = JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Loan T", "LOANS", OpeningSide: "CR"));
            JournalService.CreateVoucher(tx, _t.Admin, D(2026, 9, 10), "", [new($"ledger:{furniture.Id}", 5000), new($"ledger:{capital.Id}", Credit: 5000)]);
            JournalService.CreateVoucher(tx, _t.Admin, D(2026, 9, 10), "", [new($"cash:{_bank.Id}", 20000), new($"ledger:{loan.Id}", Credit: 20000)]);
            return 0;
        });
    }

    public void Dispose() => _t.Dispose();

    private static Dictionary<string, decimal> Rows(BalanceSheet bs) =>
        bs.Liabilities.Concat(bs.Assets).SelectMany(g => g.Rows).ToDictionary(r => r.Name, r => r.Amount);

    [Fact]
    public void ProfitAndLossUsesWeightedAverageCost()
    {
        var d = _t.Read(tx => Statements.ProfitAndLoss(tx, null, D(2026, 9, 30)));
        // Average cost = (10 x 100 + 10 x 130) / 20 = 115; 15 left = 1,725; 5 sold cost 575.
        Assert.Equal(1000.00m, d.Sales);
        Assert.Equal(1300.00m, d.Purchases);
        Assert.Equal(1000.00m, d.OpeningStockAdded);
        Assert.Equal(1725.00m, d.ClosingStock);
        Assert.Equal(575.00m, d.CostOfGoodsSold);
        Assert.Equal(425.00m, d.GrossProfit);
        Assert.Equal([("Rent", 1000.00m)], d.ExpenseCategories.Select(c => (c.Name, c.Amount)));
        Assert.Equal(-575.00m, d.NetProfit);

        // A period starting after the opening: opening stock is the stock value on the day before.
        var period = _t.Read(tx => Statements.ProfitAndLoss(tx, D(2026, 9, 3), D(2026, 9, 30)));
        Assert.Equal((2300.00m, 0.00m, 1000.00m), (period.OpeningStock, period.Purchases, period.Sales));
    }

    [Fact]
    public void BalanceSheetBalances()
    {
        var d = _t.Read(tx => Statements.BalanceSheet(tx, D(2026, 9, 30)));
        Assert.Equal(0.00m, d.Difference);
        var rows = Rows(d);
        // Bank: 100,000 + 1,130 sale - 500 - 1,000 + 20,000 loan.
        Assert.Equal(119630.00m, rows["Nabil Bank"]);
        Assert.Equal(1725.00m, rows["Closing stock"]);
        Assert.Equal(969.00m, rows["Sundry Creditors"]); // 1,469 bill - 500 paid
        Assert.Equal(39.00m, rows["VAT receivable"]);    // 130 collected - 169 paid
        Assert.Equal((5000.00m, 5000.00m, 20000.00m), (rows["Furniture T"], rows["Capital T"], rows["Loan T"]));
        Assert.Equal(-575.00m, rows["Profit & Loss A/c"]);
        // Openings: bank 100,000 + opening stock 1,000.
        Assert.Equal(101000.00m, rows["Difference in opening balances"]);
        Assert.DoesNotContain("Sundry Debtors", rows.Keys);
    }

    [Fact]
    public void CashFlowReconcilesWithCashBooks()
    {
        var d = _t.Read(tx => Statements.CashFlow(tx, D(2026, 9, 1), D(2026, 9, 30)));
        Assert.Equal(100000.00m, d.OpeningCash);
        Assert.Equal(new Dictionary<string, decimal> { ["Received from customers"] = 1130.00m, ["Paid to suppliers"] = -500.00m, ["Expenses paid"] = -1000.00m },
            d.Operating.ToDictionary(r => r.Name, r => r.Amount));
        Assert.Equal([("Loan T", 20000.00m)], d.Financing.Select(r => (r.Name, r.Amount)));
        Assert.Equal((119630.00m, 0.00m), (d.ClosingCash, d.Difference));
        Assert.Throws<ValidationError>(() => _t.Read(tx => Statements.CashFlow(tx, D(2026, 9, 30), D(2026, 9, 1))));
    }

    private void Openings()
    {
        var customerId = _t.Customer().Id;
        var vendorId = _t.Vendor().Id;
        _t.Write(tx => OpeningBalances.Save(tx, _t.Admin, new OpeningChanges
        {
            Customers = [new(customerId, 2000, "DR")],
            Vendors = [new(vendorId, 3000, "CR")],
        }));
    }

    [Fact]
    public void PartyOpeningFlowsIntoLedgerAgingAndBalanceSheet()
    {
        Openings();
        var customerId = _t.Customer().Id;
        Assert.Equal(2000.00m, _t.Read(tx => PartyLedgers.Ledger(tx, AccountConfig.Receivable, customerId, end: D(2026, 9, 30))).OpeningBalance);
        var row = _t.Read(tx => PartyLedgers.Aging(tx, AccountConfig.Receivable, D(2026, 9, 30))).Rows.Single(r => r.PartyId == customerId).Amounts;
        // The sale cancelled after 30 Sep (today here is 27 Sep, so cancelled before): only the opening is owed.
        Assert.Equal(2000.00m, row.Opening);
        var bs = _t.Read(tx => Statements.BalanceSheet(tx, D(2026, 9, 30)));
        Assert.Equal(0.00m, bs.Difference);
        var rows = Rows(bs);
        Assert.Equal((2000.00m, 3969.00m), (rows["Sundry Debtors"], rows["Sundry Creditors"]));
        Assert.Equal(100000.00m, rows["Difference in opening balances"]); // 101,000 + 2,000 Dr - 3,000 Cr
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrialBalanceBalances(bool withStart)
    {
        Openings();
        DateOnly? start = withStart ? D(2026, 9, 6) : null;
        var d = _t.Read(tx => Statements.TrialBalance(tx, start, D(2026, 9, 30)));
        Assert.Equal(0.00m, d.Difference);
        var groups = d.Groups.ToDictionary(g => g.Key);
        Assert.Equal(2000.00m, groups["SUNDRY_DEBTORS"].Debit);
        Assert.Equal(3969.00m, groups["SUNDRY_CREDITORS"].Credit);
        Assert.Equal(119630.00m, groups["CASH_BANK"].Debit);
        if (withStart)
        {
            Assert.Contains(groups["CAPITAL"].Rows, r => r.Name == "Profit & Loss A/c (before this period)");
            Assert.Equal(1725.00m, groups["STOCK"].Debit); // stock value after the 5 Sep sale
        }
        else
            Assert.Equal((1000.00m, 1300.00m), (groups["SALES"].Credit, groups["PURCHASES"].Debit));
    }

    [Fact]
    public void OpeningBalancesScreen()
    {
        var d = _t.Read(OpeningBalances.Get);
        Assert.Equal(100000.00m, d.Totals["cash"].Debit);
        Assert.Equal(1000.00m, d.Totals["stock"].Debit);
        Assert.Equal((101000.00m, 0.00m), (d.Difference.Debit, d.Difference.Credit));
        Openings();
        var capital = _t.Read(tx => JournalService.Ledgers(tx)).Single(l => l.Name == "Capital T");
        Assert.Equal(1, _t.Write(tx => OpeningBalances.Save(tx, _t.Admin, new OpeningChanges { Ledgers = [new(capital.Id, 100000, "CR")] })));
        d = _t.Read(OpeningBalances.Get);
        Assert.Equal((0.00m, 0.00m), (d.Difference.Debit, d.Difference.Credit));
        // Saving the same values again changes nothing.
        Assert.Equal(0, _t.Write(tx => OpeningBalances.Save(tx, _t.Admin, new OpeningChanges { Ledgers = [new(capital.Id, 100000, "CR")] })));
    }

    [Fact]
    public void OpeningStockRateLockedOncePosted()
    {
        var ex = Assert.Throws<BusinessError>(() => _t.Write(tx => OpeningBalances.Save(tx, _t.Admin,
            new OpeningChanges { Stock = [new(_product.Id, 10, 150)] })));
        Assert.Equal("OPENING_RATE_LOCKED", ex.Code);
        var fresh = _t.Product(stock: 0, cost: 50);
        _t.Write(tx => OpeningBalances.Save(tx, _t.Admin, new OpeningChanges { Stock = [new(fresh.Id, 4, 60)] }));
        Assert.Equal(1240.00m, _t.Read(OpeningBalances.Get).Totals["stock"].Debit); // 1,000 + 4 x 60
    }

    [Fact]
    public void OpeningBalancesNeedSectionPermission()
    {
        var staff = _t.MakeUser(Accounting.Core.Security.Role.Staff);
        var customerId = _t.Customer().Id;
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => OpeningBalances.Save(tx, staff, new OpeningChanges { Customers = [new(customerId, 1, "DR")] })));
    }
}
