using Accounting.Core;
using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Inventory;
using Accounting.Core.Parties;
using Accounting.Core.Products;
using Accounting.Core.Security;

namespace Accounting.Tests;

// Ported from backend/tests/test_products_parties.py.
public class ProductPartyTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    private static readonly ProductInput Laptop = new()
    {
        Name = "Laptop",
        SkuCode = "lap-001",
        Unit = Unit.Pcs,
        OpeningStock = 20,
        ReorderLevel = 5,
        PurchasePrice = 50000,
        SellingPrice = 65000,
    };

    private Product Create(ProductInput input) => _t.Write(tx => ProductService.Create(tx, _t.Admin, input));
    private Product Update(long id, Func<ProductInput, ProductInput> change) =>
        _t.Write(tx => ProductService.Update(tx, _t.Admin, id, change(ProductInput.From(ProductService.Get(tx, id)))));

    [Fact]
    public void CreateProductNormalisesSkuAndPostsOpeningStock()
    {
        var p = Create(Laptop);
        Assert.Equal("LAP-001", p.SkuCode);
        Assert.Equal(13.00m, p.TaxRate); // business default
        Assert.Equal(20m, _t.Read(tx => InventoryService.CalculateStock(tx, p.Id)));
        var mv = Assert.Single(_t.Read(tx => InventoryService.Movements(tx, p.Id)));
        Assert.Equal(MovementType.OpeningStock, mv.TransactionType);
        Assert.Equal(20m, mv.QuantityIn);
        Assert.Contains(_t.Read(tx => AuditLog.ForObject(tx, "products.Product", p.Id)), e => e.Action == AuditAction.Create);
    }

    [Fact]
    public void DuplicateSkuRejectedCaseInsensitive()
    {
        Create(Laptop);
        var ex = Assert.Throws<ValidationError>(() => Create(Laptop with { SkuCode = "LAP-001", Name = "Other" }));
        Assert.Equal("VALIDATION_ERROR", ex.Code);
        Assert.Equal("sku_code", ex.Field);
    }

    [Fact]
    public void EditProductIsAudited()
    {
        var p = Create(Laptop);
        var after = Update(p.Id, i => i with { SellingPrice = 70000, Name = "Laptop Pro" });
        Assert.Equal(70000.00m, after.SellingPrice);
        var audit = _t.Read(tx => AuditLog.ForObject(tx, "products.Product", p.Id)).Single(e => e.Action == AuditAction.Update);
        Assert.Contains("\"selling_price\":\"65000\"", audit.BeforeData);
        Assert.Contains("\"selling_price\":\"70000\"", audit.AfterData);
    }

    [Fact]
    public void OpeningStockHasRateAndAmount()
    {
        var p = Create(Laptop);
        Assert.Equal((50000.00m, 1000000.00m), (p.OpeningRate, p.OpeningAmount)); // rate defaults to the purchase price
        var p2 = Create(Laptop with { SkuCode = "LAP-2", OpeningRate = 48000 });
        Assert.Equal(960000.00m, p2.OpeningAmount);
        Assert.Equal(980000.00m, Update(p2.Id, i => i with { OpeningRate = 49000 }).OpeningAmount);
    }

    [Fact]
    public void OpeningStockCanBeChangedButNotBelowWhatWasSold()
    {
        var p = Create(Laptop);
        var after = Update(p.Id, i => i with { OpeningStock = 25 });
        Assert.Equal(25m, after.OpeningStock);
        Assert.Equal(25m, _t.Read(tx => InventoryService.CalculateStock(tx, p.Id)));
        var moves = _t.Read(tx => InventoryService.Movements(tx, p.Id));
        Assert.Equal([(20m, 0m), (5m, 0m)], moves.Select(m => (m.QuantityIn, m.QuantityOut)));
        Assert.Equal(moves[0].Date, moves[1].Date);

        _t.Sale(p, 22, moves[0].Date);
        var ex = Assert.Throws<BusinessError>(() => Update(p.Id, i => i with { OpeningStock = 21 }));
        Assert.Equal("OPENING_TOO_LOW", ex.Code);
        Assert.Contains("at least 22", ex.Message);
        Update(p.Id, i => i with { OpeningStock = 22 });
        Assert.Equal(0m, _t.Read(tx => InventoryService.CalculateStock(tx, p.Id)));
    }

    [Fact]
    public void OpeningStockAddedLater()
    {
        var p = Create(Laptop with { OpeningStock = 0 });
        Update(p.Id, i => i with { OpeningStock = 4 });
        Assert.Equal(4m, _t.Read(tx => InventoryService.CalculateStock(tx, p.Id)));
    }

    [Fact]
    public void NegativePriceRejected() =>
        Assert.Equal("selling_price", Assert.Throws<ValidationError>(() => Create(Laptop with { SellingPrice = -1 })).Field);

    [Fact]
    public void DeactivateAndDeleteProtection()
    {
        var p = Create(Laptop);
        var ex = Assert.Throws<BusinessError>(() => _t.Write(tx => { ProductService.Delete(tx, _t.Admin, p.Id); return 0; }));
        Assert.Equal(("PRODUCT_HAS_HISTORY", 409), (ex.Code, ex.StatusCode));
        Assert.False(_t.Write(tx => ProductService.SetActive(tx, _t.Admin, p.Id, false)).IsActive);
        Assert.Equal(0, _t.Read(tx => ProductService.List(tx, isActive: true)).Count);
        Assert.True(_t.Write(tx => ProductService.SetActive(tx, _t.Admin, p.Id, true)).IsActive);
    }

    [Fact]
    public void UnusedProductCanBeDeleted()
    {
        var p = Create(Laptop with { OpeningStock = 0 });
        _t.Write(tx => { ProductService.Delete(tx, _t.Admin, p.Id); return 0; });
        Assert.Null(_t.Read(tx => ProductService.Find(tx, p.Id)));
    }

    [Fact]
    public void ProductSearchAndPagination()
    {
        for (var i = 0; i < 30; i++)
            _t.Product($"Widget {i}");
        _t.Product("Special Gadget");
        Assert.Equal(1, _t.Read(tx => ProductService.List(tx, "gadget")).Count);
        var r = _t.Read(tx => ProductService.List(tx));
        Assert.Equal((31, 25, 25, 2), (r.Count, r.Results.Count, r.PageSize, r.TotalPages));
        Assert.Equal(31, _t.Read(tx => ProductService.List(tx, page: new PageRequest(1, 50))).Results.Count);
        Assert.Equal(25, _t.Read(tx => ProductService.List(tx, page: new PageRequest(1, 7))).PageSize); // only 25/50/100 allowed
        Assert.Equal(6, _t.Read(tx => ProductService.List(tx, page: new PageRequest(2))).Results.Count);
    }

    [Fact]
    public void StaffCannotManageProducts()
    {
        var staff = _t.MakeUser(Role.Staff);
        var ex = Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => ProductService.Create(tx, staff, Laptop)));
        Assert.Equal("PERMISSION_DENIED", ex.Code);
    }

    // -------------------------------------------------------------------- parties

    private Party CreateParty(Actor actor, PartyType type, PartyInput input) => _t.Write(tx => PartyService.Create(tx, actor, type, input));

    [Fact]
    public void CustomerQuickAddSetsTypeAndCreditDays()
    {
        var staff = _t.MakeUser(Role.Staff);
        var c = CreateParty(staff, PartyType.Customer, new PartyInput { Name = "Quick Customer", Phone = "9800000000", CreditTerms = CreditTerms.Days45 });
        Assert.Equal(PartyType.Customer, c.Type);
        Assert.Equal(45, c.CreditDays);
    }

    [Fact]
    public void CustomCreditTerms()
    {
        var v = CreateParty(_t.Admin, PartyType.Vendor, new PartyInput { Name = "V", CreditTerms = CreditTerms.Custom, CreditDays = 21 });
        Assert.Equal((21, PartyType.Vendor), (v.CreditDays, v.Type));
    }

    [Fact]
    public void PartySearchByPhoneAndPan()
    {
        CreateParty(_t.Admin, PartyType.Customer, new PartyInput { Name = "Alpha", Phone = "9811111111", PanVatNo = "123456789" });
        CreateParty(_t.Admin, PartyType.Customer, new PartyInput { Name = "Beta", Phone = "9822222222" });
        Assert.Equal(1, _t.Read(tx => PartyService.List(tx, PartyType.Customer, "98111")).Count);
        Assert.Equal(1, _t.Read(tx => PartyService.List(tx, PartyType.Customer, "123456789")).Count);
        Assert.Equal(0, _t.Read(tx => PartyService.List(tx, PartyType.Vendor)).Count);
    }

    [Fact]
    public void PartyTypeCannotChangeAndStaffCannotDeactivate()
    {
        var c = _t.Customer();
        var renamed = _t.Write(tx => PartyService.Update(tx, _t.Admin, c.Id, PartyInput.From(c) with { Name = "Renamed" }));
        Assert.Equal((PartyType.Customer, "Renamed"), (renamed.Type, renamed.Name));
        var staff = _t.MakeUser(Role.Staff);
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => PartyService.SetActive(tx, staff, c.Id, false)));
        Assert.False(_t.Write(tx => PartyService.SetActive(tx, _t.Admin, c.Id, false)).IsActive);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            _t.Write(tx => tx.Execute("UPDATE parties SET type = 'VENDOR' WHERE id = @id", new { id = c.Id })));
    }

    [Fact]
    public void CustomerLookupDoesNotExposeVendors()
    {
        var v = CreateParty(_t.Admin, PartyType.Vendor, new PartyInput { Name = "V" });
        Assert.Throws<NotFoundError>(() => _t.Read(tx => PartyService.Get(tx, v.Id, PartyType.Customer)));
    }

    [Fact]
    public void PartyValidation()
    {
        Assert.Equal("name", Assert.Throws<ValidationError>(() => CreateParty(_t.Admin, PartyType.Customer, new PartyInput { Name = "  " })).Field);
        Assert.Equal("email", Assert.Throws<ValidationError>(() => CreateParty(_t.Admin, PartyType.Customer, new PartyInput { Name = "A", Email = "nope" })).Field);
        Assert.Equal("credit_days", Assert.Throws<ValidationError>(() =>
            CreateParty(_t.Admin, PartyType.Customer, new PartyInput { Name = "A", CreditTerms = CreditTerms.Custom, CreditDays = 4000 })).Field);
        var c = CreateParty(_t.Admin, PartyType.Customer, new PartyInput { Name = "Owes us", OpeningBalance = 500, OpeningSide = "DR" });
        Assert.Equal(500m, c.SignedOpening);
    }
}
