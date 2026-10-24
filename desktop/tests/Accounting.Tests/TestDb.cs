using Accounting.Core;
using Accounting.Core.Data;
using Accounting.Core.Inventory;
using Accounting.Core.Parties;
using Accounting.Core.Products;
using Accounting.Core.Security;
using Accounting.Core.Trade;

namespace Accounting.Tests;

/// <summary>A fresh company file per test (like the pytest fixtures in backend/tests/conftest.py).</summary>
public sealed class TestDb : IDisposable
{
    public static readonly DateOnly Today = new(2026, 9, 27);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "acct-tests", Guid.NewGuid().ToString("N"));
    private int _skuCounter;

    public Database Db { get; }
    public Actor Admin { get; }

    public TestDb()
    {
        Directory.CreateDirectory(_dir);
        Db = Database.Open(Path.Combine(_dir, "company.db"));
        Db.Today = () => Today;
        Admin = Db.Write(tx => UserService.CreateFirstAdmin(tx, "admin1", "Sup3r-Secret!")).ToActor();
    }

    public Actor MakeUser(Role role) =>
        Db.Write(tx => UserService.Create(tx, Admin, $"user_{role.ToCode().ToLowerInvariant()}", "Sup3r-Secret!", role)).ToActor();

    public T Write<T>(Func<Tx, T> work) => Db.Write(work);
    public T Read<T>(Func<Tx, T> work) => Db.Read(work);

    public Product Product(string? name = null, decimal stock = 100, decimal cost = 1000, decimal price = 2000,
        decimal tax = 13, decimal reorder = 5, DateOnly? openingDate = null)
    {
        var n = ++_skuCounter;
        return Write(tx => ProductService.Create(tx, Admin, new ProductInput
        {
            Name = name ?? $"Product {n}",
            SkuCode = $"SKU-{n:0000}",
            OpeningStock = stock,
            PurchasePrice = cost,
            SellingPrice = price,
            TaxRate = tax,
            ReorderLevel = reorder,
        }, openingDate ?? new DateOnly(2026, 1, 1)));
    }

    private Party? _customer, _customer2, _vendor;

    /// <summary>"ABC Traders", 30-day terms (conftest.customer).</summary>
    public Party Customer() => _customer ??= Write(tx => PartyService.Create(tx, Admin, PartyType.Customer,
        new PartyInput { Name = "ABC Traders", CreditTerms = CreditTerms.Days30, PanVatNo = "301234567" }));

    /// <summary>"XYZ Store", 15-day terms (conftest.customer2).</summary>
    public Party Customer2() => _customer2 ??= Write(tx => PartyService.Create(tx, Admin, PartyType.Customer,
        new PartyInput { Name = "XYZ Store", CreditTerms = CreditTerms.Days15 }));

    /// <summary>"ABC Suppliers", 30-day terms (conftest.vendor).</summary>
    public Party Vendor() => _vendor ??= Write(tx => PartyService.Create(tx, Admin, PartyType.Vendor,
        new PartyInput { Name = "ABC Suppliers", CreditTerms = CreditTerms.Days30 }));

    public static LineItemInput Item(Product p, decimal qty, decimal? price = null, DiscountType? type = null, decimal value = 0) =>
        new(p.Id, qty, price, type, value);

    public SaveResult MakeSale(Party customer, IReadOnlyList<LineItemInput> items, DateOnly? date = null, PaymentInput? payment = null, Actor? actor = null) =>
        Write(tx => TradeService.Create(tx, actor ?? Admin, TradeKind.Sale,
            new DocumentInput { PartyId = customer.Id, Date = date ?? Today, Items = items, Payment = payment }));

    public TradeDocument MakePurchase(Party vendor, IReadOnlyList<LineItemInput> items, DateOnly? date = null, PaymentInput? payment = null) =>
        Write(tx => TradeService.Create(tx, Admin, TradeKind.Purchase,
            new DocumentInput { PartyId = vendor.Id, Date = date ?? Today, Items = items, Payment = payment })).Document;

    /// <summary>Purchase bill for one product from the default vendor.</summary>
    public TradeDocument Purchase(Product p, decimal qty, DateOnly date, decimal price = 100) => MakePurchase(Vendor(), [Item(p, qty, price)], date);

    /// <summary>Sales invoice for one product to the default customer.</summary>
    public TradeDocument Sale(Product p, decimal qty, DateOnly date) => MakeSale(Customer(), [Item(p, qty)], date).Document;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
