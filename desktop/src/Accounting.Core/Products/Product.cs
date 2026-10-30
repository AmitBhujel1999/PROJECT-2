namespace Accounting.Core.Products;

public enum Unit
{
    Pcs,
    Kg,
    Box,
    Ltr,
    Meter,
    Set,
    Pack,
    Dozen,
}

public static class Units
{
    public static string ToCode(this Unit unit) => unit.ToString().ToUpperInvariant();

    public static Unit Parse(string code) => Enum.Parse<Unit>(code, ignoreCase: true);

    /// <summary>Label used on screens and in messages ("Pcs", "Kg", ...).</summary>
    public static string Display(this Unit unit) => unit.ToString();
}

public sealed record Product
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string SkuCode { get; init; } = "";
    public Unit Unit { get; init; } = Unit.Pcs;
    public string Description { get; init; } = "";
    /// <summary>Customs (HS) code printed on tax invoices; may be empty.</summary>
    public string HsCode { get; init; } = "";
    public decimal OpeningStock { get; init; }
    /// <summary>Cost per unit of the opening stock.</summary>
    public decimal OpeningRate { get; init; }
    public decimal ReorderLevel { get; init; }
    public decimal PurchasePrice { get; init; }
    public decimal SellingPrice { get; init; }
    public decimal TaxRate { get; init; }
    /// <summary>Default discount for new sale lines: a percentage, or rupees off each unit (Fixed).</summary>
    public DiscountType? SaleDiscountType { get; init; }
    public decimal SaleDiscountValue { get; init; }
    /// <summary>Default discount for new purchase lines, same meaning as the sale one.</summary>
    public DiscountType? PurchaseDiscountType { get; init; }
    public decimal PurchaseDiscountValue { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTime CreatedAt { get; init; }

    public decimal OpeningAmount => Money.Round(OpeningStock * OpeningRate);

    public override string ToString() => $"{Name} ({SkuCode})";
}

/// <summary>What a user types in the product form. Null rates fall back to defaults.</summary>
public sealed record ProductInput
{
    public string Name { get; init; } = "";
    public string SkuCode { get; init; } = "";
    public Unit Unit { get; init; } = Unit.Pcs;
    public string Description { get; init; } = "";
    public string HsCode { get; init; } = "";
    public decimal OpeningStock { get; init; }
    /// <summary>Defaults to the purchase price when null.</summary>
    public decimal? OpeningRate { get; init; }
    public decimal ReorderLevel { get; init; }
    public decimal PurchasePrice { get; init; }
    public decimal SellingPrice { get; init; }
    /// <summary>Defaults to the business default tax rate when null.</summary>
    public decimal? TaxRate { get; init; }
    public DiscountType? SaleDiscountType { get; init; }
    public decimal SaleDiscountValue { get; init; }
    public DiscountType? PurchaseDiscountType { get; init; }
    public decimal PurchaseDiscountValue { get; init; }

    public static ProductInput From(Product p) => new()
    {
        Name = p.Name,
        SkuCode = p.SkuCode,
        Unit = p.Unit,
        Description = p.Description,
        HsCode = p.HsCode,
        OpeningStock = p.OpeningStock,
        OpeningRate = p.OpeningRate,
        ReorderLevel = p.ReorderLevel,
        PurchasePrice = p.PurchasePrice,
        SellingPrice = p.SellingPrice,
        TaxRate = p.TaxRate,
        SaleDiscountType = p.SaleDiscountType,
        SaleDiscountValue = p.SaleDiscountValue,
        PurchaseDiscountType = p.PurchaseDiscountType,
        PurchaseDiscountValue = p.PurchaseDiscountValue,
    };
}
