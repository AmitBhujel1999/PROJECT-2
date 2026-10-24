namespace Accounting.Core.Parties;

public enum PartyType
{
    Customer,
    Vendor,
}

public enum CreditTerms
{
    Cash,
    Days7,
    Days15,
    Days30,
    Days45,
    Days60,
    Days90,
    Custom,
}

public static class PartyCodes
{
    public static string ToCode(this PartyType type) => type == PartyType.Customer ? "CUSTOMER" : "VENDOR";
    public static PartyType ParseType(string code) => code == "CUSTOMER" ? PartyType.Customer : PartyType.Vendor;

    public static string ToCode(this CreditTerms terms) => terms switch
    {
        CreditTerms.Cash => "CASH",
        CreditTerms.Custom => "CUSTOM",
        _ => "DAYS_" + Days(terms),
    };

    public static CreditTerms ParseTerms(string code) => code switch
    {
        "CASH" => CreditTerms.Cash,
        "CUSTOM" => CreditTerms.Custom,
        _ => Enum.Parse<CreditTerms>("Days" + code["DAYS_".Length..]),
    };

    /// <summary>Days for a fixed term; null for Custom (the party's own credit days are used).</summary>
    public static int? Days(this CreditTerms terms) => terms switch
    {
        CreditTerms.Cash => 0,
        CreditTerms.Days7 => 7,
        CreditTerms.Days15 => 15,
        CreditTerms.Days30 => 30,
        CreditTerms.Days45 => 45,
        CreditTerms.Days60 => 60,
        CreditTerms.Days90 => 90,
        _ => null,
    };

    public static string Display(this CreditTerms terms) => terms switch
    {
        CreditTerms.Cash => "Cash",
        CreditTerms.Custom => "Custom",
        _ => $"{terms.Days()} Days",
    };
}

/// <summary>
/// A customer or a vendor. Balances are intentionally NOT stored: receivables and payables
/// are always derived from invoices, bills, receipts and payments. The opening balance is
/// what was owed when the books started (Dr = the party owes us, Cr = we owe the party).
/// </summary>
public sealed record Party
{
    public long Id { get; init; }
    public PartyType Type { get; init; }
    public string Name { get; init; } = "";
    public string PanVatNo { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Email { get; init; } = "";
    public string Address { get; init; } = "";
    public CreditTerms CreditTerms { get; init; }
    public int CreditDays { get; init; }
    public decimal CreditLimit { get; init; }
    public decimal OpeningBalance { get; init; }
    /// <summary>"DR" or "CR".</summary>
    public string OpeningSide { get; init; } = "DR";
    public string Notes { get; init; } = "";
    public bool IsActive { get; init; } = true;

    /// <summary>Opening balance with debit positive.</summary>
    public decimal SignedOpening => OpeningSide == "DR" ? OpeningBalance : -OpeningBalance;

    public override string ToString() => Name;
}

public sealed record PartyInput
{
    public string Name { get; init; } = "";
    public string PanVatNo { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Email { get; init; } = "";
    public string Address { get; init; } = "";
    public CreditTerms CreditTerms { get; init; } = CreditTerms.Cash;
    /// <summary>Only used when <see cref="CreditTerms"/> is Custom.</summary>
    public int CreditDays { get; init; }
    public decimal CreditLimit { get; init; }
    public decimal OpeningBalance { get; init; }
    public string OpeningSide { get; init; } = "DR";
    public string Notes { get; init; } = "";

    public static PartyInput From(Party p) => new()
    {
        Name = p.Name,
        PanVatNo = p.PanVatNo,
        Phone = p.Phone,
        Email = p.Email,
        Address = p.Address,
        CreditTerms = p.CreditTerms,
        CreditDays = p.CreditDays,
        CreditLimit = p.CreditLimit,
        OpeningBalance = p.OpeningBalance,
        OpeningSide = p.OpeningSide,
        Notes = p.Notes,
    };
}
