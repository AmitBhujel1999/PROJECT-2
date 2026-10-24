using Accounting.Core;
using Accounting.Core.Audit;
using Accounting.Core.Inventory;
using Accounting.Core.Security;

namespace Accounting.Tests;

// Ported from backend/tests/test_inventory.py.
public class InventoryTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void HistoricalStockAndBetweenDates()
    {
        var p = _t.Product(stock: 10, openingDate: D(2026, 1, 1));
        _t.Purchase(p, 20, D(2026, 3, 1));
        _t.Sale(p, 5, D(2026, 4, 1));
        _t.Sale(p, 7, D(2026, 5, 1));
        _t.Read(tx =>
        {
            Assert.Equal(18m, InventoryService.CalculateStock(tx, p.Id));
            Assert.Equal(0m, InventoryService.CalculateHistoricalStock(tx, p.Id, D(2025, 12, 31)));
            Assert.Equal(10m, InventoryService.CalculateHistoricalStock(tx, p.Id, D(2026, 2, 1)));
            Assert.Equal(30m, InventoryService.CalculateHistoricalStock(tx, p.Id, D(2026, 3, 1)));
            Assert.Equal(25m, InventoryService.CalculateHistoricalStock(tx, p.Id, D(2026, 4, 15)));
            Assert.Equal(new StockPeriod(30, 0, 5, 25), InventoryService.CalculateStockBetween(tx, p.Id, D(2026, 3, 15), D(2026, 4, 30)));
            return 0;
        });
    }

    [Fact]
    public void StockAdjustmentCreatesLedgerAndAudit()
    {
        var p = _t.Product(stock: 20);
        var adj = _t.Write(tx => InventoryService.CreateAdjustment(tx, _t.Admin, p.Id, -2, AdjustmentReason.Damaged, D(2026, 9, 27), "Damaged goods"));
        Assert.Equal("ADJ-000001", adj.AdjustmentNumber);
        Assert.Equal((20m, 18m), (adj.StockBefore, adj.StockAfter));
        Assert.Equal(18m, _t.Read(tx => InventoryService.CalculateStock(tx, p.Id)));
        var mv = _t.Read(tx => InventoryService.Movements(tx, p.Id)).Last();
        Assert.Equal((MovementType.Adjustment, 2m), (mv.TransactionType, mv.QuantityOut));
        Assert.Equal("Damaged goods: Damaged goods", mv.Notes);
        Assert.Contains(_t.Read(tx => AuditLog.ForObject(tx, "inventory.StockAdjustment", adj.Id)), e => e.Action == AuditAction.Create);
    }

    [Fact]
    public void NegativeAdjustmentCannotMakeStockNegative()
    {
        var p = _t.Product(stock: 1);
        var ex = Assert.Throws<InsufficientStockError>(() =>
            _t.Write(tx => InventoryService.CreateAdjustment(tx, _t.Admin, p.Id, -5, AdjustmentReason.Lost, D(2026, 9, 27))));
        Assert.Equal((409, "INSUFFICIENT_STOCK"), (ex.StatusCode, ex.Code));
    }

    [Fact]
    public void AdjustmentOtherReasonRequiresNotesAndZeroRejected()
    {
        var p = _t.Product();
        Assert.Equal("notes", Assert.Throws<ValidationError>(() =>
            _t.Write(tx => InventoryService.CreateAdjustment(tx, _t.Admin, p.Id, 1, AdjustmentReason.Other, D(2026, 9, 27)))).Field);
        Assert.Equal("quantity", Assert.Throws<ValidationError>(() =>
            _t.Write(tx => InventoryService.CreateAdjustment(tx, _t.Admin, p.Id, 0, AdjustmentReason.Found, D(2026, 9, 27)))).Field);
    }

    [Fact]
    public void OnlyManagersCanAdjust()
    {
        var p = _t.Product();
        foreach (var role in new[] { Role.Accountant, Role.Staff })
        {
            var user = _t.MakeUser(role);
            Assert.Throws<PermissionDeniedError>(() =>
                _t.Write(tx => InventoryService.CreateAdjustment(tx, user, p.Id, 1, AdjustmentReason.Found, D(2026, 9, 27))));
        }
        var manager = _t.MakeUser(Role.Manager);
        _t.Write(tx => InventoryService.CreateAdjustment(tx, manager, p.Id, 1, AdjustmentReason.Found, D(2026, 9, 27)));
    }

    [Fact]
    public void StockLedgerOpeningClosing()
    {
        var p = _t.Product(stock: 10, openingDate: D(2026, 1, 1));
        _t.Purchase(p, 5, D(2026, 2, 1));
        _t.Sale(p, 3, D(2026, 3, 1));
        var rows = _t.Read(tx => StockQueries.Ledger(tx, new StockQueries.LedgerFilter { ProductId = p.Id, Start = D(2026, 2, 1) })).Results;
        Assert.Equal(
            [("PURCHASE", 10m, 5m, 0m, 15m), ("SALE", 15m, 0m, 3m, 12m)],
            rows.Select(r => (r.TransactionType, r.OpeningQuantity, r.QuantityIn, r.QuantityOut, r.ClosingQuantity)));
        rows = _t.Read(tx => StockQueries.Ledger(tx, new StockQueries.LedgerFilter { ProductId = p.Id, TransactionType = "SALE" })).Results;
        Assert.Equal(12m, Assert.Single(rows).ClosingQuantity);
        Assert.Equal("admin1", rows[0].CreatedBy);
    }

    [Fact]
    public void StockRegisterStatusAndValues()
    {
        _t.Product("In", stock: 50, reorder: 5, cost: 10, price: 15);
        _t.Product("Low", stock: 4, reorder: 5);
        var outP = _t.Product("Out", stock: 2, reorder: 1);
        _t.Sale(outP, 2, TestDb.Today);
        var filter = new StockQueries.RegisterFilter();
        var rows = _t.Read(tx => StockQueries.Register(tx, filter)).Results.ToDictionary(r => r.Name);
        Assert.Equal(StockStatus.InStock, rows["In"].StockStatus);
        Assert.Equal((500.00m, 750.00m), (rows["In"].CostValue, rows["In"].RetailValue));
        Assert.Equal(StockStatus.LowStock, rows["Low"].StockStatus);
        Assert.Equal((StockStatus.OutOfStock, 2m), (rows["Out"].StockStatus, rows["Out"].TotalSold));
        var summary = _t.Read(tx => StockQueries.Summary(tx, filter));
        Assert.Equal((1, 1), (summary.LowStockCount, summary.OutOfStockCount));
        Assert.Equal(1, _t.Read(tx => StockQueries.Register(tx, filter with { Status = StockStatus.LowStock })).Count);
        Assert.Equal(["Out", "Low"], _t.Read(tx => StockQueries.LowStock(tx)).Select(r => r.Name));
    }

    [Fact]
    public void StockRegisterAsOf()
    {
        var p = _t.Product(stock: 10, openingDate: D(2026, 1, 1));
        _t.Sale(p, 4, D(2026, 6, 1));
        Assert.Equal(10m, _t.Read(tx => StockQueries.Register(tx, new StockQueries.RegisterFilter { AsOf = D(2026, 5, 31) })).Results[0].CurrentStock);
        Assert.Equal(6m, _t.Read(tx => StockQueries.Register(tx, new StockQueries.RegisterFilter { AsOf = D(2026, 6, 1) })).Results[0].CurrentStock);
    }

    [Fact]
    public void BackdatedSaleCannotMakeHistoryNegative()
    {
        // 10 units received on 1 Mar; a sale dated 1 Feb must not consume them.
        var p = _t.Product(stock: 0);
        _t.Purchase(p, 10, D(2026, 3, 1));
        var ex = Assert.Throws<InsufficientStockError>(() => _t.Sale(p, 1, D(2026, 2, 1)));
        Assert.Equal("0.000", ex.Details["available"]);
        _t.Sale(p, 1, D(2026, 3, 1));
    }

    [Fact]
    public void BackdatedSaleRespectsLaterSales()
    {
        var p = _t.Product(stock: 0);
        _t.Purchase(p, 10, D(2026, 3, 1));
        _t.Sale(p, 8, D(2026, 5, 1));
        // On 1 Apr there are 10 on hand, but 8 are already sold on 1 May -> only 2 available
        Assert.Throws<InsufficientStockError>(() => _t.Sale(p, 3, D(2026, 4, 1)));
        _t.Sale(p, 2, D(2026, 4, 1));
        Assert.Equal(8m, _t.Read(tx => InventoryService.CalculateHistoricalStock(tx, p.Id, D(2026, 4, 1))));
        Assert.Equal(0m, _t.Read(tx => InventoryService.CalculateStock(tx, p.Id)));
    }

    [Fact]
    public void FractionalQuantitiesAreExact()
    {
        var p = _t.Product(stock: 0.5m);
        _t.Purchase(p, 2.125m, TestDb.Today);
        _t.Sale(p, 0.625m, TestDb.Today);
        Assert.Equal(2.000m, _t.Read(tx => InventoryService.CalculateStock(tx, p.Id)));
    }

    [Fact]
    public void ConcurrentSalesNeverOversell()
    {
        var p = _t.Product(stock: 10);
        _t.Customer();
        var sold = 0;
        Parallel.For(0, 30, _ =>
        {
            try { _t.Sale(p, 1, TestDb.Today); Interlocked.Increment(ref sold); }
            catch (InsufficientStockError) { }
        });
        Assert.Equal(10, sold);
        Assert.Equal(0m, _t.Read(tx => InventoryService.CalculateStock(tx, p.Id)));
    }
}
