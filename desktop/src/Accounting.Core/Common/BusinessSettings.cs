using Accounting.Core.Audit;
using Accounting.Core.Data;
using Accounting.Core.Security;

namespace Accounting.Core.Common;

/// <summary>Company identity, currency and tax defaults (a single row).</summary>
public sealed record BusinessSettings
{
    public string BusinessName { get; init; } = "My Business Pvt. Ltd.";
    public string Address { get; init; } = "Kathmandu, Nepal";
    public string PanVatNo { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Email { get; init; } = "";
    public string CurrencyCode { get; init; } = "NPR";
    public string CurrencySymbol { get; init; } = "Rs.";
    public string TaxLabel { get; init; } = "VAT";
    public decimal DefaultTaxRate { get; init; } = 13m;
    public string InvoiceFooter { get; init; } = "Thank you for your business.";
    /// <summary>"AD", "BS", or "" until an admin picks one.</summary>
    public string DateSystem { get; init; } = "";

    public static BusinessSettings Get(Tx tx)
    {
        var r = tx.Row("SELECT * FROM business_settings WHERE id = 1")!;
        return new BusinessSettings
        {
            BusinessName = r.Str("business_name"),
            Address = r.Str("address"),
            PanVatNo = r.Str("pan_vat_no"),
            Phone = r.Str("phone"),
            Email = r.Str("email"),
            CurrencyCode = r.Str("currency_code"),
            CurrencySymbol = r.Str("currency_symbol"),
            TaxLabel = r.Str("tax_label"),
            DefaultTaxRate = r.Rate("default_tax_rate"),
            InvoiceFooter = r.Str("invoice_footer"),
            DateSystem = r.Str("date_system"),
        };
    }

    public static BusinessSettings Save(Tx tx, Actor actor, BusinessSettings settings)
    {
        Permissions.Require(actor, "settings.manage");
        if (string.IsNullOrWhiteSpace(settings.BusinessName))
            throw new ValidationError("business_name", "Business name is required.");
        if (settings.DefaultTaxRate is < 0 or > 100)
            throw new ValidationError("default_tax_rate", "Tax rate must be between 0 and 100.");
        if (settings.DateSystem is not ("" or "AD" or "BS"))
            throw new ValidationError("date_system", "Date system must be AD or BS.");
        var before = Get(tx);
        tx.Execute(
            """
            UPDATE business_settings SET business_name = @BusinessName, address = @Address, pan_vat_no = @PanVatNo,
                phone = @Phone, email = @Email, currency_code = @CurrencyCode, currency_symbol = @CurrencySymbol,
                tax_label = @TaxLabel, default_tax_rate = @rate, invoice_footer = @InvoiceFooter, date_system = @DateSystem,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = 1
            """,
            new
            {
                BusinessName = settings.BusinessName.Trim(), settings.Address, settings.PanVatNo, settings.Phone, settings.Email,
                settings.CurrencyCode, settings.CurrencySymbol, settings.TaxLabel, rate = Money.ToPaisa(settings.DefaultTaxRate),
                settings.InvoiceFooter, settings.DateSystem,
            });
        var after = Get(tx);
        AuditLog.Record(tx, actor, AuditAction.Update, "common.BusinessSettings", 1, after.BusinessName,
            AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }
}
