using Accounting.Core;
using Accounting.Core.CashBank;
using Accounting.Core.Common;
using Accounting.Core.Payments;
using Accounting.Core.Security;
using Accounting.Core.Trade;
using Microsoft.Data.Sqlite;
using static Accounting.Tests.TestDb;

namespace Accounting.Tests;

/// <summary>Receipt/payment allocation engine (the commands behind pay-on-save and cancellation).</summary>
public class PaymentTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    private static readonly AccountConfig R = AccountConfig.Receivable;

    private TradeDocument Invoice(decimal price, DateOnly date) => _t.MakeSale(_t.Customer(), [Item(_t.Product(price: price, tax: 0), 1)], date).Document;

    private PartyPayment Receipt(decimal amount, IReadOnlyList<AllocationRequest>? allocations = null, bool auto = false, long? accountId = null)
    {
        var customerId = _t.Customer().Id;
        return _t.Write(tx => PartyAccounts.CreatePayment(tx, _t.Admin, R, customerId, TestDb.Today, amount,
            accountId: accountId, allocations: allocations, autoAllocate: auto));
    }

    private TradeDocument Doc(TradeDocument d) => _t.Read(tx => TradeService.Get(tx, TradeKind.Sale, d.Id));

    [Fact]
    public void OnePaymentSplitAcrossInvoices()
    {
        var a = Invoice(1000, new DateOnly(2026, 8, 1));
        var b = Invoice(500, new DateOnly(2026, 9, 1));
        var r = Receipt(1200, [new(a.Id, 1000), new(b.Id, 200)]);
        Assert.Equal(("RCPT-000001", 1200.00m, 0.00m), (r.Number, r.AllocatedAmount, r.UnallocatedAmount));
        Assert.Equal(PaymentStatus.Paid, Doc(a).PaymentStatus);
        Assert.Equal((PaymentStatus.Partial, 300.00m), (Doc(b).PaymentStatus, Doc(b).BalanceDue));
    }

    [Fact]
    public void OverAllocationRejected()
    {
        var a = Invoice(1000, TestDb.Today);
        Assert.Equal("INVALID_ALLOCATION", Assert.Throws<AllocationError>(() => Receipt(500, [new(a.Id, 600)])).Code);
        Assert.Throws<AllocationError>(() => Receipt(2000, [new(a.Id, 1500)])); // more than the invoice balance
        Assert.Empty(_t.Read(tx => PartyAccounts.ListPayments(tx, R, new PaymentFilter())).Results); // rolled back
        Receipt(100);
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("UPDATE customer_receipts SET allocated_amount = amount + 1")));
    }

    [Fact]
    public void AutoAllocateOldestDueFirstAndRestIsAdvance()
    {
        var older = Invoice(300, new DateOnly(2026, 7, 1));
        var newer = Invoice(300, new DateOnly(2026, 8, 1));
        var r = Receipt(700, auto: true);
        Assert.Equal((PaymentStatus.Paid, PaymentStatus.Paid), (Doc(older).PaymentStatus, Doc(newer).PaymentStatus));
        Assert.Equal(100.00m, r.UnallocatedAmount);
        var later = Invoice(250, TestDb.Today);
        _t.Write(tx => PartyAccounts.AutoAllocate(tx, _t.Admin, R, r.Id));
        Assert.Equal((PaymentStatus.Partial, 150.00m), (Doc(later).PaymentStatus, Doc(later).BalanceDue));
    }

    [Fact]
    public void AllocationDateIsTheLaterOfPaymentAndDocument()
    {
        var future = Invoice(100, TestDb.Today.AddDays(5));
        var r = Receipt(100, [new(future.Id, 100)]);
        Assert.Equal(TestDb.Today.AddDays(5), _t.Read(tx => PartyAccounts.AllocationsForPayment(tx, R, r.Id))[0].Date);
    }

    [Fact]
    public void UnallocateVoidsAndRestoresBalance()
    {
        var a = Invoice(1000, TestDb.Today);
        var r = Receipt(1000, [new(a.Id, 1000)]);
        var alloc = _t.Read(tx => PartyAccounts.AllocationsForPayment(tx, R, r.Id))[0];
        var after = _t.Write(tx => PartyAccounts.Unallocate(tx, _t.Admin, R, alloc.Id, "wrong invoice"));
        Assert.Equal(1000.00m, after.UnallocatedAmount);
        Assert.Equal(PaymentStatus.Unpaid, Doc(a).PaymentStatus);
        Assert.Throws<AllocationError>(() => _t.Write(tx => PartyAccounts.Unallocate(tx, _t.Admin, R, alloc.Id, "again")));
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("DELETE FROM receipt_allocations")));
    }

    [Fact]
    public void CancelReceiptReopensInvoices()
    {
        var a = Invoice(1000, TestDb.Today);
        var r = Receipt(1000, [new(a.Id, 1000)]);
        var cancelled = _t.Write(tx => PartyAccounts.CancelPayment(tx, _t.Admin, R, r.Id, "Cheque bounced"));
        Assert.Equal((DocumentStatus.Cancelled, 0.00m), (cancelled.Status, cancelled.UnallocatedAmount));
        Assert.Equal(PaymentStatus.Unpaid, Doc(a).PaymentStatus);
        Assert.Equal("ALREADY_CANCELLED", Assert.Throws<BusinessError>(() => _t.Write(tx => PartyAccounts.CancelPayment(tx, _t.Admin, R, r.Id, "x"))).Code);
        Assert.Throws<AllocationError>(() => _t.Write(tx => PartyAccounts.Allocate(tx, _t.Admin, R, r.Id, [new(a.Id, 1)])));
    }

    [Fact]
    public void CannotAllocateToAnotherPartysInvoice()
    {
        var other = _t.MakeSale(_t.Customer2(), [Item(_t.Product(), 1)]).Document;
        Assert.Contains("does not belong", Assert.Throws<AllocationError>(() => Receipt(100, [new(other.Id, 100)])).Message);
    }

    [Fact]
    public void ReceiptPermissions()
    {
        var staff = _t.MakeUser(Role.Staff);
        var customerId = _t.Customer().Id;
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => PartyAccounts.CreatePayment(tx, staff, R, customerId, TestDb.Today, 10)));
    }

    [Fact]
    public void AccountsDefaultsAndMethodFollowsKind()
    {
        var r = Receipt(100);
        Assert.Equal(("Cash in Hand", PaymentMethod.Cash), (r.AccountName, r.PaymentMethod));
        var nabil = _t.Write(tx => MoneyAccounts.Create(tx, _t.Admin, new MoneyAccount { Name = "Nabil Bank", Kind = AccountKind.Bank }));
        Assert.Equal(PaymentMethod.Bank, Receipt(100, accountId: nabil.Id).PaymentMethod);
        Assert.Equal("DUPLICATE_ACCOUNT", Assert.Throws<BusinessError>(() =>
            _t.Write(tx => MoneyAccounts.Create(tx, _t.Admin, new MoneyAccount { Name = "nabil bank" }))).Code);
        Assert.Equal("ACCOUNT_IN_USE", Assert.Throws<BusinessError>(() => _t.Write(tx => { MoneyAccounts.Delete(tx, _t.Admin, nabil.Id); return 0; })).Code);
        _t.Write(tx => MoneyAccounts.SetActive(tx, _t.Admin, nabil.Id, false));
        Assert.Equal("ACCOUNT_INACTIVE", Assert.Throws<BusinessError>(() => Receipt(100, accountId: nabil.Id)).Code);
    }

    [Fact]
    public void VendorPaymentsMirrorReceipts()
    {
        var bill = _t.MakePurchase(_t.Vendor(), [Item(_t.Product(), 2, 500)]);
        var vendorId = _t.Vendor().Id;
        var pay = _t.Write(tx => PartyAccounts.CreatePayment(tx, _t.Admin, AccountConfig.Payable, vendorId, TestDb.Today, 1130,
            paymentMethod: PaymentMethod.Cheque, referenceNumber: "CHQ-1", autoAllocate: true));
        Assert.Equal(("PAY-000001", "Bank", PaymentMethod.Cheque), (pay.Number, pay.AccountName, pay.PaymentMethod));
        Assert.Equal(PaymentStatus.Paid, _t.Read(tx => TradeService.Get(tx, TradeKind.Purchase, bill.Id)).PaymentStatus);
    }
}
