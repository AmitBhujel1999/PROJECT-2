using Accounting.Core;
using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Security;
using Microsoft.Data.Sqlite;

namespace Accounting.Tests;

public class DatabaseTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void ReopeningAnExistingFileKeepsDataAndDoesNotRemigrate()
    {
        _t.Product("Kept");
        var again = Database.Open(_t.Db.Path);
        Assert.Equal(1, again.Read(tx => tx.Scalar<long>("SELECT COUNT(*) FROM products")));
    }

    [Fact]
    public void SequencesAreGapFreeAndRollBackWithTheTransaction()
    {
        Assert.Equal("INV-000001", _t.Write(tx => Sequences.Next(tx, Sequences.Sale)));
        Assert.Throws<InvalidOperationException>(() => _t.Write<string>(tx =>
        {
            Sequences.Next(tx, Sequences.Sale);
            throw new InvalidOperationException("save failed");
        }));
        Assert.Equal("INV-000002", _t.Write(tx => Sequences.Next(tx, Sequences.Sale)));
        Assert.Equal("BILL-000001", _t.Write(tx => Sequences.Next(tx, Sequences.Purchase)));
    }

    [Fact]
    public void StockLedgerIsImmutable()
    {
        _t.Product(stock: 5);
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("UPDATE stock_movements SET quantity_in = 99")));
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("DELETE FROM stock_movements")));
    }

    [Fact]
    public void DatabaseRejectsNegativeRunningBalance()
    {
        var p = _t.Product(stock: 0);
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute(
            "INSERT INTO stock_movements (product_id, date, transaction_type, quantity_out, running_balance) VALUES (@id, '2026-01-01', 'SALE', 1000, -1000)",
            new { id = p.Id })));
    }

    [Fact]
    public void DatabaseRejectsMovementInBothDirections()
    {
        var p = _t.Product(stock: 0);
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute(
            "INSERT INTO stock_movements (product_id, date, transaction_type, quantity_in, quantity_out, running_balance) VALUES (@id, '2026-01-01', 'SALE', 1000, 1000, 0)",
            new { id = p.Id })));
    }

    [Fact]
    public void AuditLogIsAppendOnly()
    {
        Assert.NotEmpty(_t.Read(tx => AuditLog.Recent(tx)));
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("UPDATE audit_log SET username = 'x'")));
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("DELETE FROM audit_log")));
    }

    [Fact]
    public void WritesFromManyThreadsNeverCollide()
    {
        var numbers = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 40, _ => numbers.Add(_t.Write(tx => Sequences.Next(tx, Sequences.Receipt))));
        Assert.Equal(40, numbers.Distinct().Count());
        Assert.Contains("RCPT-000040", numbers);
    }

    [Fact]
    public void SettingsDefaultsAndAdminOnlyChanges()
    {
        var s = _t.Read(BusinessSettings.Get);
        Assert.Equal(13m, s.DefaultTaxRate);
        Assert.Equal("NPR", s.CurrencyCode);
        var manager = _t.MakeUser(Role.Manager);
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => BusinessSettings.Save(tx, manager, s with { BusinessName = "X" })));
        var saved = _t.Write(tx => BusinessSettings.Save(tx, _t.Admin, s with { BusinessName = "Sohan Medi Health", DefaultTaxRate = 13.5m }));
        Assert.Equal(13.5m, saved.DefaultTaxRate);
        Assert.Equal("Sohan Medi Health", _t.Read(BusinessSettings.Get).BusinessName);
    }
}

public class DemoDataTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void DemoDataSeedsAConsistentCompanyOnce()
    {
        DemoData.Seed(_t.Db, _t.Admin);
        Assert.Equal(6, _t.Read(tx => Accounting.Core.Products.ProductService.List(tx)).Count);
        Assert.Equal(5, _t.Read(tx => Accounting.Core.Trade.TradeService.List(tx, Accounting.Core.Trade.TradeKind.Sale, new())).Count);
        Assert.All(_t.Read(tx => Accounting.Core.Products.ProductService.List(tx)).Results, p => Assert.EndsWith("(Demo)", p.Name));
        Assert.Equal("NOT_EMPTY", Assert.Throws<BusinessError>(() => DemoData.Seed(_t.Db, _t.Admin)).Code);
    }
}
