using Accounting.Core.Common;
using Accounting.Core.Inventory;
using Accounting.Core.Parties;
using Accounting.Core.Products;

namespace Accounting.Core.Trade;

/// <summary>
/// Sales invoices and purchase bills share columns, validation and calculation; this
/// describes what differs between them, so the shared logic is written exactly once.
/// </summary>
public sealed class TradeKind
{
    public static readonly TradeKind Sale = new()
    {
        Table = "sales",
        ItemsTable = "sale_items",
        PartyType = PartyType.Customer,
        SequenceKey = Sequences.Sale,
        Label = "Sale",
        ModelName = "sales.Sale",
        Permission = "sales",
        Movement = MovementType.Sale,
        CancelMovement = MovementType.SaleCancel,
        DefaultPrice = p => p.SellingPrice,
    };

    public static readonly TradeKind Purchase = new()
    {
        Table = "purchases",
        ItemsTable = "purchase_items",
        PartyType = PartyType.Vendor,
        SequenceKey = Sequences.Purchase,
        Label = "Purchase",
        ModelName = "purchases.Purchase",
        Permission = "purchases",
        Movement = MovementType.Purchase,
        CancelMovement = MovementType.PurchaseCancel,
        DefaultPrice = p => p.PurchasePrice,
    };

    public required string Table { get; init; }
    public required string ItemsTable { get; init; }
    public required PartyType PartyType { get; init; }
    public required string SequenceKey { get; init; }
    public required string Label { get; init; }
    public required string ModelName { get; init; }
    /// <summary>Permission prefix: "sales" or "purchases".</summary>
    public required string Permission { get; init; }
    public required string Movement { get; init; }
    public required string CancelMovement { get; init; }
    /// <summary>Unit price used when a line gives none (selling price / purchase price).</summary>
    public required Func<Product, decimal> DefaultPrice { get; init; }

    public bool IsSale => ReferenceEquals(this, Sale);
    /// <summary>Stock movement reference type: "sale" or "purchase".</summary>
    public string ReferenceType => IsSale ? "sale" : "purchase";
    public string PartyLabel => IsSale ? "Customer" : "Vendor";

    public override string ToString() => Label;
}

public sealed record TradeLine
{
    public long Id { get; init; }
    public long ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public string SkuCode { get; init; } = "";
    public string HsCode { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal GrossAmount { get; init; }
    public DiscountType? DiscountType { get; init; }
    public decimal DiscountValue { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal InvoiceDiscountShare { get; init; }
    public decimal TaxableAmount { get; init; }
    public decimal TaxRate { get; init; }
    public decimal TaxAmount { get; init; }
    public decimal LineTotal { get; init; }
}

/// <summary>A sales invoice (INV-000001) or purchase bill (BILL-000001). Never deleted, only cancelled.</summary>
public sealed record TradeDocument
{
    public long Id { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public TradeKind Kind { get; init; } = null!;
    public string Number { get; init; } = "";
    public long PartyId { get; init; }
    public string PartyName { get; init; } = "";
    /// <summary>Supplier's own invoice number (purchases only).</summary>
    public string VendorBillNumber { get; init; } = "";
    public DateOnly Date { get; init; }
    public DateOnly DueDate { get; init; }
    /// <summary>Sum of gross line amounts.</summary>
    public decimal Subtotal { get; init; }
    public decimal ItemDiscountTotal { get; init; }
    public DiscountType? DiscountType { get; init; }
    public decimal DiscountValue { get; init; }
    /// <summary>Invoice-level discount amount.</summary>
    public decimal DiscountAmount { get; init; }
    public decimal TaxableAmount { get; init; }
    public decimal TaxAmount { get; init; }
    public decimal TotalAmount { get; init; }
    /// <summary>Derived from payment allocations; never edited directly.</summary>
    public decimal AmountPaid { get; init; }
    public string PaymentStatus { get; init; } = Common.PaymentStatus.Unpaid;
    public string Status { get; init; } = DocumentStatus.Active;
    public string Notes { get; init; } = "";
    public DateTime? CancelledAt { get; init; }
    public DateOnly? CancelledOn { get; init; }
    public string CancelReason { get; init; } = "";
    public long CreatedBy { get; init; }
    public string CreatedByName { get; init; } = "";
    public long? CancelledBy { get; init; }
    public DateTime CreatedAt { get; init; }
    public IReadOnlyList<TradeLine> Lines { get; init; } = [];

    public bool IsCancelled => Status == DocumentStatus.Cancelled;
    public decimal BalanceDue => IsCancelled ? 0m : Money.Round(TotalAmount - AmountPaid);
    public decimal TotalDiscount => Money.Round(ItemDiscountTotal + DiscountAmount);

    public override string ToString() => Number;
}

public sealed record LineItemInput(
    long ProductId,
    decimal Quantity,
    /// <summary>Null uses the product's selling price (sales) or purchase price (purchases).</summary>
    decimal? UnitPrice = null,
    DiscountType? DiscountType = null,
    decimal DiscountValue = 0m);

/// <summary>
/// Payment recorded together with a sale/purchase. <see cref="PayInFull"/> uses the
/// server's own total, so a screen never has to supply the amount of a fully paid document.
/// </summary>
public sealed record PaymentInput
{
    public bool PayInFull { get; init; }
    public decimal? Amount { get; init; }
    public string? PaymentMethod { get; init; }
    public long? AccountId { get; init; }
    public string ReferenceNumber { get; init; } = "";
}

/// <summary>What a user enters for an invoice or bill. No totals: the server calculates them all.</summary>
public sealed record DocumentInput
{
    public const int MaxLines = 500;

    /// <summary>May be null only for a totals preview.</summary>
    public long? PartyId { get; init; }
    public DateOnly Date { get; init; }
    /// <summary>Null = date + the party's credit days.</summary>
    public DateOnly? DueDate { get; init; }
    public DiscountType? DiscountType { get; init; }
    public decimal DiscountValue { get; init; }
    public string Notes { get; init; } = "";
    public string VendorBillNumber { get; init; } = "";
    public IReadOnlyList<LineItemInput> Items { get; init; } = [];
    public PaymentInput? Payment { get; init; }
    /// <summary>Number already given by a phone in its own series (e.g. M1-000001); null = the next number.</summary>
    public string? Number { get; init; }
}

/// <summary>Server-calculated totals for a document being entered, with stock available for each product (sales).</summary>
public sealed record TradePreview(DocumentTotals Totals, IReadOnlyList<Product> Products, IReadOnlyDictionary<long, decimal>? AvailableStock);

public sealed record SaveResult(TradeDocument Document, IReadOnlyList<string> Warnings);

public sealed record TradeFilter
{
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public long? PartyId { get; init; }
    /// <summary>Part of the document number.</summary>
    public string? Number { get; init; }
    public string? PaymentStatus { get; init; }
    public string? Status { get; init; }
    /// <summary>Matches number, vendor bill number, party name, PAN/VAT or phone.</summary>
    public string? Search { get; init; }
    /// <summary>"-date" (default), "total_amount", "due_date", "number" ...</summary>
    public string? Ordering { get; init; }
}
