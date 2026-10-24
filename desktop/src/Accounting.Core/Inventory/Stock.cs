namespace Accounting.Core.Inventory;

public static class MovementType
{
    public const string OpeningStock = "OPENING_STOCK";
    public const string Purchase = "PURCHASE";
    public const string Sale = "SALE";
    public const string Adjustment = "ADJUSTMENT";
    public const string PurchaseCancel = "PURCHASE_CANCEL";
    public const string SaleCancel = "SALE_CANCEL";

    public static string Display(string type) => type switch
    {
        OpeningStock => "Opening stock",
        Purchase => "Purchase",
        Sale => "Sale",
        Adjustment => "Adjustment",
        PurchaseCancel => "Purchase cancellation",
        SaleCancel => "Sale cancellation",
        _ => type,
    };
}

public static class AdjustmentReason
{
    public const string Damaged = "DAMAGED";
    public const string Expired = "EXPIRED";
    public const string Lost = "LOST";
    public const string CountCorrection = "COUNT_CORRECTION";
    public const string Found = "FOUND";
    public const string Return = "RETURN";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All = [Damaged, Expired, Lost, CountCorrection, Found, Return, Other];

    public static string Display(string reason) => reason switch
    {
        Damaged => "Damaged goods",
        Expired => "Expired",
        Lost => "Lost / theft",
        CountCorrection => "Physical count correction",
        Found => "Found / surplus",
        Return => "Returned goods",
        Other => "Other",
        _ => reason,
    };
}

public static class StockStatus
{
    public const string InStock = "IN_STOCK";
    public const string LowStock = "LOW_STOCK";
    public const string OutOfStock = "OUT_OF_STOCK";
}

/// <summary>One immutable stock ledger row.</summary>
public sealed record StockMovement(
    long Id, long ProductId, DateOnly Date, string TransactionType, string ReferenceType, long? ReferenceId,
    string ReferenceNumber, decimal QuantityIn, decimal QuantityOut, decimal RunningBalance, decimal UnitCost,
    string Notes, DateTime CreatedAt, long? CreatedBy);

public sealed record StockAdjustment(
    long Id, string AdjustmentNumber, long ProductId, DateOnly Date, decimal Quantity, string Reason, string Notes,
    decimal StockBefore, decimal StockAfter, DateTime CreatedAt, long? CreatedBy)
{
    public string ReasonDisplay => AdjustmentReason.Display(Reason);
}

public sealed record StockPeriod(decimal Opening, decimal QuantityIn, decimal QuantityOut, decimal Closing);

/// <summary>One stock register line: totals, stock, valuation and status for a product.</summary>
public sealed record StockRegisterRow(
    long ProductId, string Name, string SkuCode, string Unit, decimal ReorderLevel, decimal PurchasePrice,
    decimal SellingPrice, bool IsActive, decimal TotalPurchased, decimal TotalSold, decimal TotalAdjusted,
    decimal CurrentStock, decimal CostValue, decimal RetailValue, string StockStatus);

public sealed record StockRegisterSummary(int ProductCount, decimal TotalCostValue, decimal TotalRetailValue, int LowStockCount, int OutOfStockCount);

/// <summary>One stock ledger line with the true historical opening and closing quantity.</summary>
public sealed record StockLedgerRow(
    long Id, DateOnly Date, long ProductId, string ProductName, string SkuCode, string Unit, string TransactionType,
    string ReferenceType, long? ReferenceId, string ReferenceNumber, decimal OpeningQuantity, decimal QuantityIn,
    decimal QuantityOut, decimal ClosingQuantity, decimal RunningBalance, string Notes, DateTime CreatedAt, string? CreatedBy)
{
    public string TransactionTypeDisplay => MovementType.Display(TransactionType);
}
