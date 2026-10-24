namespace Accounting.Core.Common;

public static class DocumentStatus
{
    public const string Active = "ACTIVE";
    public const string Cancelled = "CANCELLED";
}

public static class PaymentStatus
{
    public const string Paid = "PAID";
    public const string Partial = "PARTIAL";
    public const string Unpaid = "UNPAID";

    public static string For(decimal total, decimal paid) =>
        paid <= 0 ? (total > 0 ? Unpaid : Paid)
        : paid >= total ? Paid
        : Partial;
}

public static class PaymentMethod
{
    public const string Cash = "CASH";
    public const string Bank = "BANK";
    public const string Cheque = "CHEQUE";
    public const string Online = "ONLINE";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All = [Cash, Bank, Cheque, Online, Other];

    public static string Display(string method) => method switch
    {
        Cash => "Cash",
        Bank => "Bank",
        Cheque => "Cheque",
        Online => "Online Transfer",
        _ => "Other",
    };
}

public static class DiscountTypes
{
    public static string? ToCode(this DiscountType? type) => type switch
    {
        DiscountType.Percentage => "PERCENTAGE",
        DiscountType.Fixed => "FIXED",
        _ => null,
    };

    public static DiscountType? Parse(string? code) => code switch
    {
        "PERCENTAGE" => DiscountType.Percentage,
        "FIXED" => DiscountType.Fixed,
        null or "" => null,
        _ => throw new ValidationError("discount_type", $"\"{code}\" is not a valid choice."),
    };
}
