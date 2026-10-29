using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Inventory;
using Accounting.Core.Security;

namespace Accounting.Core.Products;

public static class ProductService
{
    private const string Model = "products.Product";

    public static Product Create(Tx tx, Actor actor, ProductInput input, DateOnly? openingDate = null)
    {
        Permissions.Require(actor, "products.manage");
        tx.RequireWrite();
        var data = Normalise(tx, input, excludeId: null);
        var id = tx.Insert(
            """
            INSERT INTO products (name, sku_code, unit, description, hs_code, opening_stock, opening_rate, reorder_level,
                                  purchase_price, selling_price, tax_rate)
            VALUES (@Name, @SkuCode, @Unit, @Description, @HsCode, @OpeningStock, @OpeningRate, @ReorderLevel,
                    @PurchasePrice, @SellingPrice, @TaxRate)
            """, data);
        var product = Get(tx, id);
        InventoryService.PostOpeningStock(tx, actor, product, openingDate ?? tx.Today);
        AuditLog.Record(tx, actor, AuditAction.Create, Model, id, product.ToString(), after: AuditLog.Snapshot(product));
        return product;
    }

    public static Product Update(Tx tx, Actor actor, long id, ProductInput input)
    {
        Permissions.Require(actor, "products.manage");
        tx.RequireWrite();
        var before = Get(tx, id);
        var data = Normalise(tx, input, excludeId: id);
        if (data.OpeningStock != Money.ToMilli(before.OpeningStock))
            InventoryService.ChangeOpeningStock(tx, actor, before with { OpeningRate = Money.FromPaisa(data.OpeningRate) },
                Money.FromMilli(data.OpeningStock));
        tx.Execute(
            """
            UPDATE products SET name = @Name, sku_code = @SkuCode, unit = @Unit, description = @Description, hs_code = @HsCode,
                opening_stock = @OpeningStock, opening_rate = @OpeningRate, reorder_level = @ReorderLevel,
                purchase_price = @PurchasePrice, selling_price = @SellingPrice, tax_rate = @TaxRate,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @id
            """, new
            {
                id, data.Name, data.SkuCode, data.Unit, data.Description, data.HsCode, data.OpeningStock, data.OpeningRate,
                data.ReorderLevel, data.PurchasePrice, data.SellingPrice, data.TaxRate,
            });
        var after = Get(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Update, Model, id, after.ToString(), AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    public static Product SetActive(Tx tx, Actor actor, long id, bool active)
    {
        Permissions.Require(actor, "products.manage");
        var before = Get(tx, id);
        tx.Execute("UPDATE products SET is_active = @active, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = @id",
            new { id, active });
        var after = Get(tx, id);
        AuditLog.Record(tx, actor, active ? AuditAction.Activate : AuditAction.Deactivate, Model, id, after.ToString(),
            AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    /// <summary>Physically delete only products that were never used anywhere.</summary>
    public static void Delete(Tx tx, Actor actor, long id)
    {
        Permissions.Require(actor, "products.manage");
        var product = Get(tx, id);
        if (HasHistory(tx, id))
            throw new BusinessError("This product has transaction history and cannot be deleted. Deactivate it instead.",
                "PRODUCT_HAS_HISTORY", 409);
        AuditLog.Record(tx, actor, AuditAction.Delete, Model, id, product.ToString(), before: AuditLog.Snapshot(product));
        tx.Execute("DELETE FROM products WHERE id = @id", new { id });
    }

    /// <summary>True when any table that references products (stock, sales, purchases, ...) has a row for this one.</summary>
    public static bool HasHistory(Tx tx, long id)
    {
        var tables = tx.Query<string>(
            """
            SELECT m.name FROM sqlite_master m, pragma_foreign_key_list(m.name) f
            WHERE m.type = 'table' AND f."table" = 'products' AND f."from" = 'product_id'
            """).ToList();
        return tables.Any(t => tx.Scalar<long>($"SELECT EXISTS (SELECT 1 FROM \"{t}\" WHERE product_id = @id)", new { id }) == 1);
    }

    public static Product Get(Tx tx, long id) => Find(tx, id) ?? throw new NotFoundError("Product not found.");

    public static Product? Find(Tx tx, long id) =>
        tx.Rows("SELECT * FROM products WHERE id = @id", new { id }).Select(Map).FirstOrDefault();

    /// <summary>Products ordered by name. <paramref name="search"/> matches name, SKU or description.</summary>
    public static Page<Product> List(Tx tx, string? search = null, bool? isActive = null, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        const string where =
            """
            WHERE (@isActive IS NULL OR is_active = @isActive)
              AND (@search IS NULL OR name LIKE @search OR sku_code LIKE @search OR description LIKE @search)
            """;
        var args = new { isActive, search = LikePattern(search), limit = page.Size, offset = page.Offset };
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM products {where}", args);
        var rows = tx.Rows($"SELECT * FROM products {where} ORDER BY name COLLATE NOCASE, id LIMIT @limit OFFSET @offset", args)
            .Select(Map).ToList();
        return new Page<Product>(rows, (int)count, page.Page, page.Size);
    }

    /// <summary>"%term%" with LIKE wildcards in the term escaped; null when there is nothing to search.</summary>
    public static string? LikePattern(string? search) =>
        string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim().Replace("%", "").Replace("_", "") + "%";

    public static Product Map(Row r) => new()
    {
        Id = r.Long("id"),
        Name = r.Str("name"),
        SkuCode = r.Str("sku_code"),
        Unit = Units.Parse(r.Str("unit")),
        Description = r.Str("description"),
        HsCode = r.Str("hs_code"),
        OpeningStock = r.Qty("opening_stock"),
        OpeningRate = r.Money("opening_rate"),
        ReorderLevel = r.Qty("reorder_level"),
        PurchasePrice = r.Money("purchase_price"),
        SellingPrice = r.Money("selling_price"),
        TaxRate = r.Rate("tax_rate"),
        IsActive = r.Bool("is_active"),
        CreatedAt = r.Timestamp("created_at"),
    };

    private sealed record StoredProduct(
        string Name, string SkuCode, string Unit, string Description, string HsCode, long OpeningStock, long OpeningRate,
        long ReorderLevel, long PurchasePrice, long SellingPrice, long TaxRate);

    private static StoredProduct Normalise(Tx tx, ProductInput input, long? excludeId)
    {
        var name = input.Name.Trim();
        var sku = input.SkuCode.Trim().ToUpperInvariant();
        if (name.Length == 0)
            throw new ValidationError("name", "Name is required.");
        if (sku.Length == 0)
            sku = NextCode(tx);
        if (tx.Scalar<long>("SELECT COUNT(*) FROM products WHERE sku_code = @sku AND id IS NOT @excludeId", new { sku, excludeId }) > 0)
            throw new ValidationError("sku_code", "A product with this SKU already exists.");
        NotNegative("opening_stock", input.OpeningStock);
        NotNegative("reorder_level", input.ReorderLevel);
        NotNegative("purchase_price", input.PurchasePrice);
        NotNegative("selling_price", input.SellingPrice);
        var openingRate = input.OpeningRate ?? input.PurchasePrice;
        NotNegative("opening_rate", openingRate);
        var taxRate = input.TaxRate ?? BusinessSettings.Get(tx).DefaultTaxRate;
        if (taxRate is < 0 or > 100)
            throw new ValidationError("tax_rate", "Tax rate must be between 0 and 100.");
        return new StoredProduct(name, sku, input.Unit.ToCode(), input.Description.Trim(), input.HsCode.Trim(), Money.ToMilli(input.OpeningStock),
            Money.ToPaisa(openingRate), Money.ToMilli(input.ReorderLevel), Money.ToPaisa(input.PurchasePrice),
            Money.ToPaisa(input.SellingPrice), Money.ToPaisa(Money.Rate(taxRate)));
    }

    /// <summary>Code for an item saved without one: ITEM-0001, ITEM-0002, ... (first free number).</summary>
    private static string NextCode(Tx tx)
    {
        var n = tx.Scalar<long>("SELECT COUNT(*) FROM products") + 1;
        while (tx.Scalar<long>("SELECT COUNT(*) FROM products WHERE sku_code = @code", new { code = $"ITEM-{n:0000}" }) > 0)
            n++;
        return $"ITEM-{n:0000}";
    }

    private static void NotNegative(string field, decimal value)
    {
        if (value < 0)
            throw new ValidationError(field, "Ensure this value is greater than or equal to 0.");
    }
}
