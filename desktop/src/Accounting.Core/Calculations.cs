namespace Accounting.Core;

/// <summary>
/// The single financial calculation engine for sales and purchases, ported
/// line for line from backend/apps/common/calculations.py.
///
/// Calculation order (identical for sales invoices and purchase bills):
///
///   Quantity x Unit Price            -> Gross Amount        (per line, rounded)
///   Gross Amount - Item Discount     -> Net Item Amount     (per line)
///   Sum(Net Item Amounts)            -> Net Subtotal
///   Invoice-level Discount           -> allocated to lines pro-rata by net amount
///   Net Item Amount - Invoice share  -> Taxable Amount      (per line)
///   Taxable Amount x Line Tax Rate   -> Tax Amount          (per line, rounded)
///   Taxable Amount + Tax Amount      -> Line Total
///   Sum(line values)                 -> Document totals
///
/// The invoice discount is allocated to lines because products carry different
/// tax rates, so VAT must be computed on each line's own taxable amount. Shares
/// are rounded and the remainder goes to the largest line, so they sum exactly.
/// </summary>
public enum DiscountType
{
    Percentage,
    Fixed,
}

public sealed record LineInput(
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    DiscountType? DiscountType = null,
    decimal DiscountValue = 0m,
    object? Ref = null);

public sealed class LineResult
{
    public object? Ref { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal GrossAmount { get; init; }
    public DiscountType? DiscountType { get; init; }
    public decimal DiscountValue { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal NetAmount { get; init; }
    public decimal InvoiceDiscountShare { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal TaxRate { get; init; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
}

public sealed class DocumentTotals
{
    public List<LineResult> Lines { get; init; } = [];
    public decimal Subtotal { get; init; }              // sum of gross amounts
    public decimal ItemDiscountTotal { get; init; }
    public decimal NetSubtotal { get; init; }           // after item discounts
    public DiscountType? DiscountType { get; init; }
    public decimal DiscountValue { get; init; }
    public decimal DiscountAmount { get; init; }        // invoice-level discount
    public decimal TotalDiscount { get; init; }
    public decimal TaxableAmount { get; init; }
    public decimal TaxAmount { get; init; }
    public decimal TotalAmount { get; init; }
}

public static class Calculations
{
    /// <summary>Return the rounded discount amount on <paramref name="baseAmount"/> and validate it.</summary>
    public static decimal ComputeDiscount(decimal baseAmount, DiscountType? type, decimal value, string label = "Discount")
    {
        if (type is null || value == 0)
            return 0m;
        if (value < 0)
            throw new CalculationError($"{label} cannot be negative.", "INVALID_DISCOUNT");
        if (type == DiscountType.Percentage)
        {
            if (value > Money.Hundred)
                throw new CalculationError($"{label} percentage cannot exceed 100%.", "INVALID_DISCOUNT");
            return Money.Round(baseAmount * value / Money.Hundred);
        }
        var amount = Money.Round(value);
        if (amount > baseAmount)
            throw new CalculationError(
                $"{label} ({amount:0.00}) cannot exceed the amount it applies to ({Money.Round(baseAmount):0.00}).",
                "INVALID_DISCOUNT");
        return amount;
    }

    public static LineResult CalculateLine(LineInput line, int index = 0)
    {
        var quantity = Money.Qty(line.Quantity);
        var unitPrice = Money.Round(line.UnitPrice);
        var taxRate = Money.Rate(line.TaxRate);
        var label = $"Line {index + 1}";
        if (quantity <= 0)
            throw new CalculationError($"{label}: quantity must be greater than zero.", "INVALID_QUANTITY");
        if (unitPrice < 0)
            throw new CalculationError($"{label}: price cannot be negative.", "INVALID_PRICE");
        if (taxRate < 0 || taxRate > Money.Hundred)
            throw new CalculationError($"{label}: tax rate must be between 0 and 100.", "INVALID_TAX_RATE");
        var gross = Money.Round(quantity * unitPrice);
        var discount = ComputeDiscount(gross, line.DiscountType, line.DiscountValue, $"{label} discount");
        var dtype = discount > 0 || line.DiscountValue > 0 ? line.DiscountType : null;
        return new LineResult
        {
            Ref = line.Ref,
            Quantity = quantity,
            UnitPrice = unitPrice,
            GrossAmount = gross,
            DiscountType = dtype,
            DiscountValue = dtype is null ? 0m : line.DiscountValue,
            DiscountAmount = discount,
            NetAmount = gross - discount,
            TaxableAmount = gross - discount,
            TaxRate = taxRate,
        };
    }

    /// <summary>Allocate an invoice discount across lines pro-rata by net amount.</summary>
    private static void Allocate(decimal amount, List<LineResult> lines)
    {
        var netTotal = lines.Sum(l => l.NetAmount);
        if (amount <= 0 || netTotal <= 0)
            return;
        var allocated = 0m;
        foreach (var ln in lines)
        {
            ln.InvoiceDiscountShare = Money.Round(amount * ln.NetAmount / netTotal);
            allocated += ln.InvoiceDiscountShare;
        }
        var remainder = amount - allocated;
        if (remainder != 0)
            lines.MaxBy(l => l.NetAmount)!.InvoiceDiscountShare += remainder;
        if (lines.Any(l => l.InvoiceDiscountShare > l.NetAmount))
            throw new CalculationError("Invoice discount allocation exceeded a line amount.", "INVALID_DISCOUNT");
    }

    public static DocumentTotals CalculateDocument(IReadOnlyList<LineInput> lines, DiscountType? discountType = null, decimal discountValue = 0m)
    {
        if (lines.Count == 0)
            throw new CalculationError("At least one item is required.", "NO_ITEMS");
        var results = lines.Select((l, i) => CalculateLine(l, i)).ToList();
        var subtotal = results.Sum(r => r.GrossAmount);
        var itemDiscounts = results.Sum(r => r.DiscountAmount);
        var netSubtotal = subtotal - itemDiscounts;
        var invoiceDiscount = ComputeDiscount(netSubtotal, discountType, discountValue, "Invoice discount");
        Allocate(invoiceDiscount, results);
        foreach (var r in results)
        {
            r.TaxableAmount = Money.Round(r.NetAmount - r.InvoiceDiscountShare);
            r.TaxAmount = Money.Round(r.TaxableAmount * r.TaxRate / Money.Hundred);
            r.Total = Money.Round(r.TaxableAmount + r.TaxAmount);
        }
        var taxable = results.Sum(r => r.TaxableAmount);
        var tax = results.Sum(r => r.TaxAmount);
        var hasInvoiceDiscount = discountType is not null && discountValue > 0;
        return new DocumentTotals
        {
            Lines = results,
            Subtotal = Money.Round(subtotal),
            ItemDiscountTotal = Money.Round(itemDiscounts),
            NetSubtotal = Money.Round(netSubtotal),
            DiscountType = hasInvoiceDiscount ? discountType : null,
            DiscountValue = hasInvoiceDiscount ? discountValue : 0m,
            DiscountAmount = invoiceDiscount,
            TotalDiscount = Money.Round(itemDiscounts + invoiceDiscount),
            TaxableAmount = Money.Round(taxable),
            TaxAmount = Money.Round(tax),
            TotalAmount = Money.Round(taxable + tax),
        };
    }
}
