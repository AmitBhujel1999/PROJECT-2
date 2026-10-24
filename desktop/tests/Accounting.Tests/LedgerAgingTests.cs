using Accounting.Core.Common;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Products;
using Accounting.Core.Security;
using Accounting.Core.Trade;
using static Accounting.Tests.TestDb;

namespace Accounting.Tests;

// Ported from backend/tests/test_receivables_payables.py (ledgers, balances, aging, summary).
public class LedgerAgingTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly Product _unit;
    private readonly Actor _accountant;

    private static readonly AccountConfig R = AccountConfig.Receivable;
    private static readonly AccountConfig P = AccountConfig.Payable;
    private static readonly DateOnly AsOf = new(2026, 9, 27);

    public LedgerAgingTests()
    {
        // Tax 0% and price 1 so invoice totals equal quantities (easy arithmetic).
        _unit = _t.Product("Unit Item", stock: 1_000_000, price: 1, cost: 1, tax: 0, openingDate: D(2024, 1, 1));
        _accountant = _t.MakeUser(Role.Accountant);
    }

    public void Dispose() => _t.Dispose();

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private TradeDocument Invoice(decimal amount, DateOnly date, DateOnly? due = null, Party? customer = null)
    {
        var sale = _t.MakeSale(customer ?? _t.Customer(), [Item(_unit, amount)], date).Document;
        if (due is { } d)
            _t.Write(tx => tx.Execute("UPDATE sales SET due_date = @d WHERE id = @id", new { d, id = sale.Id }));
        return _t.Read(tx => TradeService.Get(tx, TradeKind.Sale, sale.Id));
    }

    private PartyPayment Receipt(decimal amount, DateOnly date, params AllocationRequest[] allocations)
    {
        var customerId = _t.Customer().Id;
        return _t.Write(tx => PartyAccounts.CreatePayment(tx, _accountant, R, customerId, date, amount, PaymentMethod.Cash, allocations: allocations));
    }

    private decimal Balance(DateOnly asOf) => _t.Read(tx => PartyLedgers.BalanceAsOf(tx, R, asOf, _t.Customer().Id));
    private AgingSummary Aging(DateOnly asOf, AccountConfig? cfg = null) => _t.Read(tx => PartyLedgers.Aging(tx, cfg ?? R, asOf));

    [Fact]
    public void SpecAllocationExample()
    {
        var inv1 = Invoice(30000, D(2026, 8, 1));
        var inv8 = Invoice(20000, D(2026, 8, 5));
        Invoice(15000, D(2026, 8, 10));
        var r = Receipt(40000, D(2026, 9, 1), new(inv1.Id, 30000), new(inv8.Id, 10000));
        Assert.Equal(0.00m, r.UnallocatedAmount);
        Assert.Equal(25000.00m, Balance(D(2026, 9, 30)));
    }

    [Fact]
    public void UnallocatedReceiptIsAdvance()
    {
        Receipt(5000, D(2026, 9, 1));
        var totals = Aging(AsOf).Totals;
        Assert.Equal((0.00m, 5000.00m, -5000.00m), (totals.Total, totals.Unallocated, totals.NetBalance));
    }

    [Fact]
    public void VendorAgingWithAdvance()
    {
        var p = _t.Product(tax: 0);
        var b1 = _t.MakePurchase(_t.Vendor(), [Item(p, 10, 100)], D(2026, 8, 1));
        var b2 = _t.MakePurchase(_t.Vendor(), [Item(p, 5, 100)], D(2026, 8, 15));
        var vendorId = _t.Vendor().Id;
        _t.Write(tx => PartyAccounts.CreatePayment(tx, _accountant, P, vendorId, D(2026, 9, 1), 1200, PaymentMethod.Cheque,
            referenceNumber: "CHQ-9", allocations: [new(b1.Id, 1000), new(b2.Id, 200)]));
        _t.Write(tx => PartyAccounts.CreatePayment(tx, _accountant, P, vendorId, D(2026, 9, 5), 800));
        var totals = Aging(AsOf, P).Totals;
        Assert.Equal((300.00m, 800.00m, -500.00m), (totals.Total, totals.Unallocated, totals.NetBalance));
    }

    [Fact]
    public void CustomerLedgerRunningBalanceSpecExample()
    {
        Invoice(25000, D(2026, 8, 20)); // before period -> opening 25,000
        Invoice(50000, D(2026, 9, 1));
        Receipt(20000, D(2026, 9, 5));
        Invoice(25000, D(2026, 9, 12));
        Receipt(10000, D(2026, 9, 20));
        var data = _t.Read(tx => PartyLedgers.Ledger(tx, R, _t.Customer().Id, D(2026, 9, 1), D(2026, 9, 27)));
        Assert.Equal(25000.00m, data.OpeningBalance);
        Assert.Equal(
            [
                ("INV-000002", "Invoice", 50000.00m, 0.00m, 75000.00m),
                ("RCPT-000001", "Receipt", 0.00m, 20000.00m, 55000.00m),
                ("INV-000003", "Invoice", 25000.00m, 0.00m, 80000.00m),
                ("RCPT-000002", "Receipt", 0.00m, 10000.00m, 70000.00m),
            ],
            data.Entries.Select(e => (e.Reference, e.TransactionType, e.Debit, e.Credit, e.Balance)));
        Assert.Equal(70000.00m, data.ClosingBalance);
        Assert.Equal(D(2026, 10, 1), data.Entries[0].DueDate);
        Assert.Equal("Receipt - Cash in Hand", data.Entries[1].Description);
        Assert.Equal((75000.00m, 30000.00m), (data.TotalDebit, data.TotalCredit));
    }

    [Fact]
    public void LedgerShowsCancellationAsReversal()
    {
        var sale = Invoice(500, D(2026, 9, 1));
        _t.Write(tx => TradeService.Cancel(tx, _t.Admin, TradeKind.Sale, sale.Id, "Error"));
        var data = _t.Read(tx => PartyLedgers.Ledger(tx, R, _t.Customer().Id));
        Assert.Equal(["Invoice", "Invoice Cancellation"], data.Entries.Select(e => e.TransactionType));
        Assert.Equal("Invoice INV-000001 cancelled: Error", data.Entries[1].Description);
        Assert.Equal(0.00m, data.ClosingBalance);
        Assert.Equal(0.00m, Balance(TestDb.Today));
        // Before the cancellation date the invoice was still owed (history is not rewritten)
        Assert.Equal(500.00m, Balance(D(2026, 9, 1)));
    }

    [Fact]
    public void VendorLedgerCreditBillsDebitPayments()
    {
        var p = _t.Product(tax: 0);
        _t.MakePurchase(_t.Vendor(), [Item(p, 10, 100)], D(2026, 9, 1));
        var vendorId = _t.Vendor().Id;
        _t.Write(tx => PartyAccounts.CreatePayment(tx, _accountant, P, vendorId, D(2026, 9, 10), 400, PaymentMethod.Bank));
        var entries = _t.Read(tx => PartyLedgers.Ledger(tx, P, vendorId)).Entries;
        Assert.Equal((1000.00m, 0.00m, 1000.00m), (entries[0].Credit, entries[0].Debit, entries[0].Balance));
        Assert.Equal((0.00m, 400.00m, 600.00m), (entries[1].Credit, entries[1].Debit, entries[1].Balance));
    }

    [Fact]
    public void CancelledReceiptAppearsAsReversalInLedger()
    {
        Invoice(1000, D(2026, 9, 1));
        var r = Receipt(400, D(2026, 9, 10));
        _t.Write(tx => PartyAccounts.CancelPayment(tx, _t.Admin, R, r.Id, "Bounced"));
        var data = _t.Read(tx => PartyLedgers.Ledger(tx, R, _t.Customer().Id));
        Assert.Equal(["Invoice", "Receipt", "Receipt Cancellation"], data.Entries.Select(e => e.TransactionType));
        Assert.Equal([1000.00m, 600.00m, 1000.00m], data.Entries.Select(e => e.Balance));
        Assert.Equal(600.00m, Balance(D(2026, 9, 26)));
    }

    [Fact]
    public void HistoricalBalance()
    {
        Invoice(1000, D(2026, 7, 1));
        Receipt(300, D(2026, 8, 1));
        Assert.Equal(0.00m, Balance(D(2026, 6, 30)));
        Assert.Equal(1000.00m, Balance(D(2026, 7, 15)));
        Assert.Equal(700.00m, Balance(D(2026, 8, 1)));
    }

    public static TheoryData<int, string> Buckets => new()
    {
        { -5, AgingBucket.Current }, { 0, AgingBucket.Current }, { 1, AgingBucket.Days1To30 }, { 30, AgingBucket.Days1To30 },
        { 31, AgingBucket.Days31To60 }, { 60, AgingBucket.Days31To60 }, { 61, AgingBucket.Days61To90 }, { 90, AgingBucket.Days61To90 },
        { 91, AgingBucket.Days91To120 }, { 120, AgingBucket.Days91To120 }, { 121, AgingBucket.Over120 }, { 400, AgingBucket.Over120 },
    };

    [Theory]
    [MemberData(nameof(Buckets))]
    public void AgingBuckets(int daysOverdue, string bucket)
    {
        var due = AsOf.AddDays(-daysOverdue);
        Invoice(100, (due < AsOf ? due : AsOf).AddDays(-1), due);
        var data = Aging(AsOf);
        var row = Assert.Single(data.Rows).Amounts;
        Assert.Equal(100.00m, row[bucket]);
        Assert.Equal(100.00m, row.Total);
        Assert.Equal(100.00m, row.Buckets.Values.Sum());
    }

    [Fact]
    public void PartiallyPaidInvoiceAgesOnlyOutstanding()
    {
        var inv = Invoice(1000, D(2026, 7, 1)); // due 2026-07-31 -> 58 days
        Receipt(600, D(2026, 8, 1), new AllocationRequest(inv.Id, 600));
        var row = Aging(AsOf).Rows[0].Amounts;
        Assert.Equal((400.00m, 400.00m), (row[AgingBucket.Days31To60], row.Total));
    }

    [Fact]
    public void AgingAsOfHistoricalDate()
    {
        var inv = Invoice(1000, D(2026, 7, 1)); // due 07-31
        Receipt(1000, D(2026, 9, 1), new AllocationRequest(inv.Id, 1000));
        Invoice(50, D(2026, 9, 20));
        // Today: first invoice paid, only the 50 is open and not yet due
        var now = Aging(AsOf).Totals;
        Assert.Equal((50.00m, 50.00m), (now.Total, now[AgingBucket.Current]));
        // As of 2026-08-15 the 1,000 invoice was 15 days overdue and the 50 did not exist yet
        var then = Aging(D(2026, 8, 15)).Totals;
        Assert.Equal((1000.00m, 1000.00m), (then.Total, then[AgingBucket.Days1To30]));
        // Before the invoice date nothing was outstanding
        Assert.Empty(Aging(D(2026, 6, 30)).Rows);
    }

    [Fact]
    public void AgingHistoricalIgnoresLaterUnallocation()
    {
        var inv = Invoice(1000, D(2026, 7, 1));
        var r = Receipt(1000, D(2026, 8, 1), new AllocationRequest(inv.Id, 1000));
        var allocationId = _t.Read(tx => PartyAccounts.AllocationsForPayment(tx, R, r.Id))[0].Id;
        _t.Write(tx => PartyAccounts.Unallocate(tx, _t.Admin, R, allocationId, "wrong"));
        Assert.Equal(0.00m, Aging(D(2026, 9, 1)).Totals.Total); // voided on 09-27, so still settled on 09-01
        Assert.Equal((1000.00m, 1000.00m), (Aging(AsOf).Totals.Total, Aging(AsOf).Totals.Unallocated));
    }

    [Fact]
    public void AgingHistoricalIgnoresLaterCancellation()
    {
        var sale = Invoice(700, D(2026, 8, 1));
        _t.Write(tx => TradeService.Cancel(tx, _t.Admin, TradeKind.Sale, sale.Id, "x"));
        Assert.Empty(Aging(TestDb.Today).Rows);
        Assert.Equal(700.00m, Aging(D(2026, 8, 20)).Totals.Total);
    }

    [Fact]
    public void AgingSearchAndOneRowPerParty()
    {
        Invoice(100, D(2026, 9, 1));
        Invoice(200, D(2026, 9, 2));
        Invoice(300, D(2026, 9, 3), customer: _t.Customer2());
        var all = Aging(AsOf);
        Assert.Equal(["ABC Traders", "XYZ Store"], all.Rows.Select(r => r.PartyName));
        Assert.Equal((2, 300.00m), (all.Rows[0].DocumentCount, all.Rows[0].Amounts.Total));
        Assert.Equal(600.00m, all.Totals.Total);
        Assert.Single(_t.Read(tx => PartyLedgers.Aging(tx, R, AsOf, search: "xyz")).Rows);
    }

    [Fact]
    public void PartyAgingDetail()
    {
        Invoice(100, D(2026, 6, 1));
        var detail = _t.Read(tx => PartyLedgers.AgingDetail(tx, R, _t.Customer().Id, AsOf));
        var doc = Assert.Single(detail.Documents);
        Assert.Equal((88, AgingBucket.Days61To90), (doc.DaysOverdue, doc.Bucket));
        Assert.Equal(100.00m, detail.Totals[AgingBucket.Days61To90]);
    }

    [Fact]
    public void CustomerSummary()
    {
        Invoice(1000, D(2026, 7, 1));
        Invoice(500, TestDb.Today);
        Receipt(200, D(2026, 8, 1));
        var s = _t.Read(tx => PartyLedgers.Summary(tx, R, _t.Customer()));
        Assert.Equal(1500.00m, s.TotalDocuments);
        Assert.Equal(200.00m, s.TotalPayments);
        Assert.Equal(1500.00m, s.Outstanding);
        Assert.Equal(200.00m, s.Unallocated);
        Assert.Equal(1300.00m, s.NetBalance);
        Assert.Equal(1000.00m, s.Overdue);
        Assert.Equal(TestDb.Today, s.LastTransactionDate);
        Assert.Null(s.AvailableCredit);
    }

    [Fact]
    public void OpeningBalanceFlowsIntoBalanceLedgerAndAging()
    {
        var customer = _t.Write(tx => PartyService.Create(tx, _t.Admin, PartyType.Customer,
            new PartyInput { Name = "Old Customer", OpeningBalance = 5000, OpeningSide = "DR", CreditLimit = 8000 }));
        var vendor = _t.Write(tx => PartyService.Create(tx, _t.Admin, PartyType.Vendor,
            new PartyInput { Name = "Old Vendor", OpeningBalance = 3000, OpeningSide = "CR" }));
        Assert.Equal(5000.00m, _t.Read(tx => PartyLedgers.BalanceAsOf(tx, R, D(2020, 1, 1), customer.Id)));
        Assert.Equal(3000.00m, _t.Read(tx => PartyLedgers.BalanceAsOf(tx, P, D(2020, 1, 1), vendor.Id))); // we owe the vendor
        var ledger = _t.Read(tx => PartyLedgers.Ledger(tx, R, customer.Id));
        Assert.Equal((5000.00m, 5000.00m), (ledger.OpeningBalance, ledger.ClosingBalance));
        var row = Assert.Single(_t.Read(tx => PartyLedgers.Aging(tx, R, AsOf, customer.Id)).Rows).Amounts;
        Assert.Equal((5000.00m, 5000.00m), (row.Opening, row.NetBalance));
        Assert.Equal(3000.00m, _t.Read(tx => PartyLedgers.Summary(tx, R, customer)).AvailableCredit);
        var overview = _t.Read(tx => PartyLedgers.Overview(tx, R));
        Assert.Equal((5000.00m, 5000.00m), (overview.Outstanding, overview.Opening));
    }

    [Fact]
    public void Overview()
    {
        Invoice(1000, D(2026, 7, 1));
        Invoice(500, TestDb.Today);
        Receipt(200, D(2026, 8, 1));
        var o = _t.Read(tx => PartyLedgers.Overview(tx, R));
        Assert.Equal((1500.00m, 1000.00m, 200.00m), (o.Outstanding, o.Overdue, o.Unallocated));
    }
}
