using Accounting.Core.Common;
using Accounting.Core.Parties;
using Accounting.Core.Trade;

namespace Accounting.Core.Payments;

/// <summary>
/// Receivables (customers: invoices vs. receipts) and payables (vendors: bills vs. payments)
/// are mirror images; this describes one side so the engine is written once.
/// </summary>
public sealed class AccountConfig
{
    public static readonly AccountConfig Receivable = new()
    {
        Key = "receivable",
        Documents = TradeKind.Sale,
        PaymentTable = "customer_receipts",
        AllocationTable = "receipt_allocations",
        SequenceKey = Sequences.Receipt,
        DocLabel = "Invoice",
        PayLabel = "Receipt",
        PaymentModel = "receivables.CustomerReceipt",
        Permission = "receipts",
        DocumentIsDebit = true,
    };

    public static readonly AccountConfig Payable = new()
    {
        Key = "payable",
        Documents = TradeKind.Purchase,
        PaymentTable = "vendor_payments",
        AllocationTable = "payment_allocations",
        SequenceKey = Sequences.Payment,
        DocLabel = "Bill",
        PayLabel = "Payment",
        PaymentModel = "payables.VendorPayment",
        Permission = "payments",
        DocumentIsDebit = false,
    };

    public static AccountConfig For(TradeKind kind) => kind.IsSale ? Receivable : Payable;

    public required string Key { get; init; }
    public required TradeKind Documents { get; init; }
    public required string PaymentTable { get; init; }
    public required string AllocationTable { get; init; }
    public required string SequenceKey { get; init; }
    public required string DocLabel { get; init; }
    public required string PayLabel { get; init; }
    public required string PaymentModel { get; init; }
    /// <summary>Permission prefix: "receipts" or "payments".</summary>
    public required string Permission { get; init; }
    /// <summary>Customer ledger: invoice = debit. Vendor ledger: bill = credit.</summary>
    public required bool DocumentIsDebit { get; init; }

    public PartyType PartyType => Documents.PartyType;
    public string DocTable => Documents.Table;

    public override string ToString() => Key;
}

/// <summary>Money received from a customer (RCPT-000001) or paid to a vendor (PAY-000001).</summary>
public sealed record PartyPayment
{
    public long Id { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public AccountConfig Config { get; init; } = null!;
    public string Number { get; init; } = "";
    public long PartyId { get; init; }
    public string PartyName { get; init; } = "";
    public DateOnly Date { get; init; }
    public decimal Amount { get; init; }
    /// <summary>Cache of active allocations, kept by the engine.</summary>
    public decimal AllocatedAmount { get; init; }
    public string PaymentMethod { get; init; } = Common.PaymentMethod.Cash;
    public long AccountId { get; init; }
    public string AccountName { get; init; } = "";
    public string ReferenceNumber { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Status { get; init; } = DocumentStatus.Active;
    public DateTime? CancelledAt { get; init; }
    public DateOnly? CancelledOn { get; init; }
    public string CancelReason { get; init; } = "";
    public long CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; }

    public bool IsCancelled => Status == DocumentStatus.Cancelled;
    /// <summary>Not allocated to any invoice/bill: an advance.</summary>
    public decimal UnallocatedAmount => IsCancelled ? 0m : Money.Round(Amount - AllocatedAmount);

    public override string ToString() => Number;
}

/// <summary>Part of a payment settled against one invoice/bill. Voided, never deleted.</summary>
public sealed record Allocation(
    long Id, long PaymentId, string PaymentNumber, long DocumentId, string DocumentNumber, decimal Amount,
    DateOnly Date, bool IsActive, DateTime? VoidedAt, DateOnly? VoidedOn, string VoidReason);

/// <summary>One requested allocation: this much of the payment against that document.</summary>
public sealed record AllocationRequest(long DocumentId, decimal Amount);

public sealed record PaymentFilter
{
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public long? PartyId { get; init; }
    public string? Status { get; init; }
    public long? AccountId { get; init; }
    public string? PaymentMethod { get; init; }
    /// <summary>Only active payments with something left to allocate.</summary>
    public bool UnallocatedOnly { get; init; }
    /// <summary>Matches number, reference number or party name.</summary>
    public string? Search { get; init; }
}

public class AllocationError(string message) : BusinessError(message, "INVALID_ALLOCATION");
