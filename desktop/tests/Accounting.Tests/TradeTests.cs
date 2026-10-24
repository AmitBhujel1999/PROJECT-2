using Accounting.Core;
using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Inventory;
using Accounting.Core.Payments;
using Accounting.Core.Products;
using Accounting.Core.Security;
using Accounting.Core.Trade;
using Microsoft.Data.Sqlite;
using static Accounting.Tests.TestDb;

namespace Accounting.Tests;

// Ported from backend/tests/test_purchases_sales.py.
public class TradeTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    private const DiscountType Pct = DiscountType.Percentage;
    private const DiscountType Fix = DiscountType.Fixed;
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private decimal Stock(Product p) => _t.Read(tx => InventoryService.CalculateStock(tx, p.Id));

    private SaveResult Save(TradeKind kind, DocumentInput input, Actor? actor = null) =>
        _t.Write(tx => TradeService.Create(tx, actor ?? _t.Admin, kind, input));

    private TradeDocument Cancel(TradeKind kind, long id, string reason, Actor? actor = null) =>
        _t.Write(tx => TradeService.Cancel(tx, actor ?? _t.Admin, kind, id, reason));

    private int Count(TradeKind kind) => _t.Read(tx => TradeService.List(tx, kind, new TradeFilter())).Count;

    // ------------------------------------------------------------------ purchases

    [Fact]
    public void PurchaseCreationDiscountsVatAndStockIncrease()
    {
        var p = _t.Product(stock: 10, cost: 1000);
        var d = Save(TradeKind.Purchase, new DocumentInput
        {
            PartyId = _t.Vendor().Id,
            Date = D(2026, 9, 1),
            VendorBillNumber = "VB-77",
            Items = [Item(p, 10, 1000, Pct, 10)],
        }).Document;
        Assert.Equal("BILL-000001", d.Number);
        Assert.Equal("VB-77", d.VendorBillNumber);
        Assert.Equal(10000.00m, d.Subtotal);
        Assert.Equal(1000.00m, d.ItemDiscountTotal);
        Assert.Equal(9000.00m, d.TaxableAmount);
        Assert.Equal(1170.00m, d.TaxAmount);
        Assert.Equal(10170.00m, d.TotalAmount);
        Assert.Equal(PaymentStatus.Unpaid, d.PaymentStatus);
        Assert.Equal(D(2026, 10, 1), d.DueDate); // 30-day vendor terms
        Assert.Equal(20m, Stock(p));
        var mv = _t.Read(tx => InventoryService.Movements(tx, p.Id)).Single(m => m.ReferenceType == "purchase" && m.ReferenceId == d.Id);
        Assert.Equal((MovementType.Purchase, 900.00m), (mv.TransactionType, mv.UnitCost));
    }

    [Fact]
    public void PurchaseInvoiceLevelDiscount()
    {
        TradeDocument d = Save(TradeKind.Purchase, new DocumentInput
        {
            PartyId = _t.Vendor().Id,
            Date = D(2026, 9, 1),
            DiscountType = Fix,
            DiscountValue = 500,
            Items = [Item(_t.Product(), 2, 1000), Item(_t.Product(), 3, 1000, Fix, 300)],
        }).Document;
        Assert.Equal(5000.00m, d.Subtotal);
        Assert.Equal(300.00m, d.ItemDiscountTotal);
        Assert.Equal(500.00m, d.DiscountAmount);
        Assert.Equal(4200.00m, d.TaxableAmount);
        Assert.Equal(546.00m, d.TaxAmount);
        Assert.Equal(4746.00m, d.TotalAmount);
        Assert.Equal(800.00m, d.TotalDiscount);
        Assert.Equal(d.TaxableAmount, d.Lines.Sum(l => l.TaxableAmount));
    }

    [Fact]
    public void PurchasePaidOnCreationSetsStatus()
    {
        var p = _t.Product();
        var d = _t.MakePurchase(_t.Vendor(), [Item(p, 1, 1000)], D(2026, 9, 1), new PaymentInput { Amount = 1130, PaymentMethod = "CASH" });
        Assert.Equal((PaymentStatus.Paid, 0.00m), (d.PaymentStatus, d.BalanceDue));
        d = _t.MakePurchase(_t.Vendor(), [Item(p, 1, 1000)], D(2026, 9, 1), new PaymentInput { Amount = 500, PaymentMethod = "BANK" });
        Assert.Equal((PaymentStatus.Partial, 630.00m), (d.PaymentStatus, d.BalanceDue));
        var payment = _t.Read(tx => PartyAccounts.ListPayments(tx, AccountConfig.Payable, new PaymentFilter())).Results.First();
        Assert.Equal(("PAY-000002", "Bank", "BANK"), (payment.Number, payment.AccountName, payment.PaymentMethod));
    }

    [Fact]
    public void DueDateOverrideRequiresPermission()
    {
        var p = _t.Product();
        var input = new DocumentInput { PartyId = _t.Customer().Id, Date = D(2026, 9, 1), DueDate = D(2026, 9, 5), Items = [Item(p, 1)] };
        var staff = _t.MakeUser(Role.Staff);
        var ex = Assert.Throws<BusinessError>(() => Save(TradeKind.Sale, input, staff));
        Assert.Equal((403, "DUE_DATE_OVERRIDE_DENIED"), (ex.StatusCode, ex.Code));
        Assert.Equal(D(2026, 9, 5), Save(TradeKind.Sale, input).Document.DueDate);
        // Staff may still give the standard due date explicitly.
        Assert.Equal(D(2026, 10, 1), Save(TradeKind.Sale, input with { DueDate = D(2026, 10, 1) }, staff).Document.DueDate);
    }

    [Fact]
    public void CancelPurchaseReversesStockAndBlocksIfConsumed()
    {
        var p = _t.Product(stock: 0);
        var bill = _t.MakePurchase(_t.Vendor(), [Item(p, 10, 100)]);
        var cancelled = Cancel(TradeKind.Purchase, bill.Id, "Wrong vendor");
        Assert.Equal(DocumentStatus.Cancelled, cancelled.Status);
        Assert.Equal((TestDb.Today, "Wrong vendor"), (cancelled.CancelledOn!.Value, cancelled.CancelReason));
        Assert.Equal(0m, Stock(p));
        Assert.Single(_t.Read(tx => InventoryService.Movements(tx, p.Id)), m => m.TransactionType == MovementType.PurchaseCancel);
        Assert.Equal("ALREADY_CANCELLED", Assert.Throws<BusinessError>(() => Cancel(TradeKind.Purchase, bill.Id, "x")).Code);

        var bill2 = _t.MakePurchase(_t.Vendor(), [Item(p, 10, 100)]);
        _t.MakeSale(_t.Customer(), [Item(p, 8)]);
        var ex = Assert.Throws<InsufficientStockError>(() => Cancel(TradeKind.Purchase, bill2.Id, "oops"));
        Assert.Equal(409, ex.StatusCode);
        Assert.StartsWith("Cannot cancel BILL-000002: stock of", ex.Message);
    }

    [Fact]
    public void CancelNeedsReasonAndManager()
    {
        var sale = _t.Sale(_t.Product(), 1, TestDb.Today);
        Assert.Equal("REASON_REQUIRED", Assert.Throws<BusinessError>(() => Cancel(TradeKind.Sale, sale.Id, "  ")).Code);
        Assert.Throws<PermissionDeniedError>(() => Cancel(TradeKind.Sale, sale.Id, "x", _t.MakeUser(Role.Accountant)));
    }

    [Fact]
    public void DocumentsCannotBeDeletedOrLinesChanged()
    {
        var sale = _t.Sale(_t.Product(), 1, TestDb.Today);
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("DELETE FROM sales WHERE id = @id", new { id = sale.Id })));
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("UPDATE sale_items SET quantity = 1")));
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("UPDATE sales SET amount_paid = total_amount + 1")));
        Cancel(TradeKind.Sale, sale.Id, "test");
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("UPDATE sales SET status = 'ACTIVE'")));
    }

    // ------------------------------------------------------------------ sales

    [Fact]
    public void SaleCreationDiscountsVatAndStockDecrease()
    {
        var a = _t.Product("Product A", stock: 120, price: 500);
        var b = _t.Product("Product B", stock: 18, price: 750);
        var d = Save(TradeKind.Sale, new DocumentInput
        {
            PartyId = _t.Customer().Id,
            Date = D(2026, 9, 27),
            Items = [Item(a, 5, null, Pct, 10), Item(b, 2)],
            DiscountType = Pct,
            DiscountValue = 10,
        }).Document;
        Assert.Equal("INV-000001", d.Number);
        Assert.Equal(4000.00m, d.Subtotal);
        Assert.Equal(250.00m, d.ItemDiscountTotal);
        Assert.Equal(375.00m, d.DiscountAmount);
        Assert.Equal(3375.00m, d.TaxableAmount);
        Assert.Equal(438.75m, d.TaxAmount);
        Assert.Equal(3813.75m, d.TotalAmount);
        Assert.Equal(D(2026, 10, 27), d.DueDate);
        Assert.Equal(500.00m, d.Lines[0].UnitPrice); // auto-populated selling price
        Assert.Equal((115m, 16m), (Stock(a), Stock(b)));
        Assert.Equal(("ABC Traders", "admin1"), (d.PartyName, d.CreatedByName));
        Assert.Contains(_t.Read(tx => AuditLog.ForObject(tx, "sales.Sale", d.Id)), e => e.Action == AuditAction.Create);
    }

    [Fact]
    public void InsufficientStockRejectedAndNothingSaved()
    {
        var a = _t.Product("Product A", stock: 7);
        var b = _t.Product("Product B", stock: 100);
        var input = new DocumentInput { PartyId = _t.Customer().Id, Date = D(2026, 9, 27), Items = [Item(b, 1), Item(a, 10)] };
        var ex = Assert.Throws<InsufficientStockError>(() => Save(TradeKind.Sale, input));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("Product A", ex.Message);
        Assert.Equal(("7.000", "10.000"), (ex.Details["available"], ex.Details["requested"]));
        Assert.Equal(0, Count(TradeKind.Sale));
        Assert.Equal(100m, Stock(b)); // rolled back
        // The invoice number was not consumed either
        Assert.Equal("INV-000001", Save(TradeKind.Sale, input with { Items = [Item(a, 7)] }).Document.Number);
        Assert.Equal(0m, Stock(a));
    }

    [Fact]
    public void SameProductOnTwoLinesIsValidatedTogether()
    {
        var a = _t.Product(stock: 5);
        Assert.Throws<InsufficientStockError>(() => _t.MakeSale(_t.Customer(), [Item(a, 3), Item(a, 3)]));
    }

    [Fact]
    public void LowStockWarningReturned()
    {
        var a = _t.Product("Low Item", stock: 10, reorder: 5);
        Assert.Contains(_t.MakeSale(_t.Customer(), [Item(a, 6)]).Warnings, w => w.Contains("Low Item") && w.Contains("4 Pcs left"));
        Assert.Contains(_t.MakeSale(_t.Customer(), [Item(a, 4)]).Warnings, w => w == "Low Item is now out of stock.");
    }

    [Fact]
    public void InactiveCustomerAndProductRejected()
    {
        var a = _t.Product();
        _t.Write(tx => ProductService.SetActive(tx, _t.Admin, a.Id, false));
        Assert.Equal("PRODUCT_INACTIVE", Assert.Throws<BusinessError>(() => _t.MakeSale(_t.Customer(), [Item(a, 1)])).Code);
        var b = _t.Product();
        var customerId = _t.Customer().Id;
        _t.Write(tx => Accounting.Core.Parties.PartyService.SetActive(tx, _t.Admin, customerId, false));
        Assert.Equal("PARTY_INACTIVE", Assert.Throws<BusinessError>(() => _t.MakeSale(_t.Customer(), [Item(b, 1)])).Code);
    }

    [Fact]
    public void CalculatePreviewReturnsServerTotalsAndStock()
    {
        var a = _t.Product(stock: 12, price: 100);
        var preview = _t.Read(tx => TradeService.Calculate(tx, _t.Admin, TradeKind.Sale,
            new DocumentInput { PartyId = _t.Customer().Id, Date = D(2026, 9, 27), Items = [Item(a, 2, null, Fix, 20)] }));
        Assert.Equal(203.40m, preview.Totals.TotalAmount);
        Assert.Equal(12m, preview.AvailableStock![a.Id]);
        Assert.Equal(0, Count(TradeKind.Sale));
    }

    [Fact]
    public void PreviewWithoutParty()
    {
        var p = _t.Product(price: 100);
        var preview = _t.Read(tx => TradeService.Calculate(tx, _t.Admin, TradeKind.Sale,
            new DocumentInput { Date = D(2026, 9, 27), Items = [Item(p, 1)] }));
        Assert.Equal(113.00m, preview.Totals.TotalAmount);
        Assert.Equal("party", Assert.Throws<ValidationError>(() => _t.Write(tx => TradeService.Create(tx, _t.Admin, TradeKind.Sale,
            new DocumentInput { Date = D(2026, 9, 27), Items = [Item(p, 1)] }))).Field);
    }

    [Fact]
    public void CancelSaleRestoresStockAndReleasesPayment()
    {
        var a = _t.Product(stock: 10, price: 100);
        var sale = _t.MakeSale(_t.Customer(), [Item(a, 4)], payment: new PaymentInput { Amount = 452, PaymentMethod = "CASH" }).Document;
        Assert.Equal(PaymentStatus.Paid, sale.PaymentStatus);
        Assert.Equal(6m, Stock(a));
        var cancelled = Cancel(TradeKind.Sale, sale.Id, "Customer returned");
        Assert.Equal((0.00m, PaymentStatus.Unpaid, 0.00m), (cancelled.AmountPaid, cancelled.PaymentStatus, cancelled.BalanceDue));
        Assert.Equal(10m, Stock(a));
        var receipt = Assert.Single(_t.Read(tx => PartyAccounts.ListPayments(tx, AccountConfig.Receivable, new PaymentFilter())).Results);
        Assert.Equal(452.00m, receipt.UnallocatedAmount); // now a customer advance
        var alloc = Assert.Single(_t.Read(tx => PartyAccounts.AllocationsForPayment(tx, AccountConfig.Receivable, receipt.Id)));
        Assert.Equal((false, "INV-000001 cancelled", TestDb.Today), (alloc.IsActive, alloc.VoidReason, alloc.VoidedOn!.Value));
    }

    [Fact]
    public void SaleRequiresItems() =>
        Assert.Equal("items", Assert.Throws<ValidationError>(() => _t.MakeSale(_t.Customer(), [])).Field);

    [Fact]
    public void SaleToVendorRejected() =>
        Assert.Equal("CUSTOMER_NOT_FOUND", Assert.Throws<BusinessError>(() => _t.MakeSale(_t.Vendor(), [Item(_t.Product(), 1)])).Code);

    [Fact]
    public void StaffCannotCreatePurchases()
    {
        var staff = _t.MakeUser(Role.Staff);
        Assert.Throws<PermissionDeniedError>(() => Save(TradeKind.Purchase,
            new DocumentInput { PartyId = _t.Vendor().Id, Date = TestDb.Today, Items = [Item(_t.Product(), 1)] }, staff));
    }

    [Fact]
    public void StaffCanTakeCashOnASale()
    {
        var staff = _t.MakeUser(Role.Staff);
        var sale = _t.MakeSale(_t.Customer(), [Item(_t.Product(price: 100), 1)], payment: new PaymentInput { PayInFull = true }, actor: staff).Document;
        Assert.Equal(PaymentStatus.Paid, sale.PaymentStatus);
    }

    [Fact]
    public void ConcurrentSalesCannotOversell()
    {
        // Four simultaneous sales of 6 units against 10 in stock: exactly one wins.
        var product = _t.Product(stock: 10);
        var customer = _t.Customer();
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 4, _ =>
        {
            try { _t.MakeSale(customer, [Item(product, 6)]); results.Add("ok"); }
            catch (InsufficientStockError) { results.Add("insufficient"); }
        });
        Assert.Equal(["insufficient", "insufficient", "insufficient", "ok"], results.Order());
        Assert.Equal(4m, Stock(product));
        Assert.Equal(1, Count(TradeKind.Sale));
    }

    [Fact]
    public void ConcurrentDocumentNumbersAreUnique()
    {
        var product = _t.Product(stock: 1000);
        var customer = _t.Customer();
        var numbers = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 8, _ => numbers.Add(_t.MakeSale(customer, [Item(product, 1)]).Document.Number));
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"INV-{i:000000}"), numbers.Order());
    }

    [Fact]
    public void BackdatedDueDateValidation() =>
        Assert.Equal("due_date", Assert.Throws<ValidationError>(() => Save(TradeKind.Sale, new DocumentInput
        {
            PartyId = _t.Customer().Id, Date = D(2026, 9, 27), DueDate = D(2026, 9, 1), Items = [Item(_t.Product(), 1)],
        })).Field);

    [Fact]
    public void SaleListFilters()
    {
        var p = _t.Product(stock: 100);
        _t.MakeSale(_t.Customer(), [Item(p, 1)], D(2026, 8, 1));
        _t.MakeSale(_t.Customer2(), [Item(p, 1)], D(2026, 9, 1));
        int Count(TradeFilter f) => _t.Read(tx => TradeService.List(tx, TradeKind.Sale, f)).Count;
        Assert.Equal(1, Count(new TradeFilter { Start = D(2026, 8, 15) }));
        Assert.Equal(1, Count(new TradeFilter { PartyId = _t.Customer().Id }));
        Assert.Equal(1, Count(new TradeFilter { Search = "XYZ" }));
        Assert.Equal(1, Count(new TradeFilter { Search = "301234567" }));
        Assert.Equal(1, Count(new TradeFilter { Number = "000002" }));
        Assert.Equal(2, Count(new TradeFilter { PaymentStatus = PaymentStatus.Unpaid }));
        var newest = _t.Read(tx => TradeService.List(tx, TradeKind.Sale, new TradeFilter())).Results[0];
        Assert.Equal("INV-000002", newest.Number);
    }

    [Fact]
    public void PayInFullUsesServerTotal()
    {
        var p = _t.Product(price: 100);
        var d = _t.MakeSale(_t.Customer(), [Item(p, 3)], payment: new PaymentInput { PayInFull = true, PaymentMethod = "CASH" }).Document;
        Assert.Equal((PaymentStatus.Paid, 339.00m, 339.00m), (d.PaymentStatus, d.AmountPaid, d.TotalAmount));
        Assert.Equal("amount", Assert.Throws<ValidationError>(() =>
            _t.MakeSale(_t.Customer(), [Item(p, 3)], payment: new PaymentInput { PaymentMethod = "CASH" })).Field);
        Assert.Equal("INVALID_AMOUNT", Assert.Throws<BusinessError>(() =>
            _t.MakeSale(_t.Customer(), [Item(p, 3)], payment: new PaymentInput { Amount = 340 })).Code);
    }

    [Fact]
    public void ProductWithSalesCannotBeDeleted()
    {
        var p = _t.Product(stock: 0);
        _t.Purchase(p, 1, TestDb.Today);
        Assert.True(_t.Read(tx => ProductService.HasHistory(tx, p.Id)));
    }
}
