using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Products;
using Accounting.Core.Security;

namespace Accounting.Core.Parties;

/// <summary>Customers and vendors. Parties are never deleted (ledgers depend on them); deactivate instead.</summary>
public static class PartyService
{
    private const string Model = "parties.Party";

    /// <summary>Anyone may add a party (quick-add from sales entry); editing needs parties.manage.</summary>
    public static Party Create(Tx tx, Actor actor, PartyType type, PartyInput input)
    {
        Permissions.Require(actor, "parties.create");
        tx.RequireWrite();
        var d = Normalise(input);
        var id = tx.Insert(
            """
            INSERT INTO parties (type, name, pan_vat_no, phone, email, address, credit_terms, credit_days, credit_limit,
                                 opening_balance, opening_side, notes)
            VALUES (@type, @Name, @PanVatNo, @Phone, @Email, @Address, @Terms, @CreditDays, @CreditLimit,
                    @OpeningBalance, @OpeningSide, @Notes)
            """, Params(d, new { type = type.ToCode() }));
        var party = Get(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Create, Model, id, party.Name, after: AuditLog.Snapshot(party));
        return party;
    }

    /// <summary>Edit a party. Its type never changes (ledgers depend on it).</summary>
    public static Party Update(Tx tx, Actor actor, long id, PartyInput input)
    {
        Permissions.Require(actor, "parties.manage");
        tx.RequireWrite();
        var before = Get(tx, id);
        var d = Normalise(input);
        tx.Execute(
            """
            UPDATE parties SET name = @Name, pan_vat_no = @PanVatNo, phone = @Phone, email = @Email, address = @Address,
                credit_terms = @Terms, credit_days = @CreditDays, credit_limit = @CreditLimit, opening_balance = @OpeningBalance,
                opening_side = @OpeningSide, notes = @Notes, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @id
            """, Params(d, new { id }));
        var after = Get(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Update, Model, id, after.Name, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    public static Party SetActive(Tx tx, Actor actor, long id, bool active)
    {
        Permissions.Require(actor, "parties.manage");
        var before = Get(tx, id);
        tx.Execute("UPDATE parties SET is_active = @active, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = @id",
            new { id, active });
        var after = Get(tx, id);
        AuditLog.Record(tx, actor, active ? AuditAction.Activate : AuditAction.Deactivate, Model, id, after.Name,
            AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    public static Party Get(Tx tx, long id, PartyType? type = null)
    {
        var party = tx.Rows("SELECT * FROM parties WHERE id = @id", new { id }).Select(Map).FirstOrDefault();
        if (party is null || (type is not null && party.Type != type))
            throw new NotFoundError(type == PartyType.Vendor ? "Vendor not found." : type == PartyType.Customer ? "Customer not found." : "Party not found.");
        return party;
    }

    /// <summary>Parties of one type ordered by name. <paramref name="search"/> matches name, phone, PAN/VAT or email.</summary>
    public static Page<Party> List(Tx tx, PartyType type, string? search = null, bool? isActive = null, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        const string where =
            """
            WHERE type = @type AND (@isActive IS NULL OR is_active = @isActive)
              AND (@search IS NULL OR name LIKE @search OR phone LIKE @search OR pan_vat_no LIKE @search OR email LIKE @search)
            """;
        var args = new { type = type.ToCode(), isActive, search = ProductService.LikePattern(search), limit = page.Size, offset = page.Offset };
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM parties {where}", args);
        var rows = tx.Rows($"SELECT * FROM parties {where} ORDER BY name COLLATE NOCASE, id LIMIT @limit OFFSET @offset", args)
            .Select(Map).ToList();
        return new Page<Party>(rows, (int)count, page.Page, page.Size);
    }

    private sealed record Stored(
        string Name, string PanVatNo, string Phone, string Email, string Address, string Terms, int CreditDays,
        long CreditLimit, long OpeningBalance, string OpeningSide, string Notes);

    private static object Params(Stored d, object extra)
    {
        var p = new Dapper.DynamicParameters(d);
        p.AddDynamicParams(extra);
        return p;
    }

    private static Stored Normalise(PartyInput input)
    {
        var name = input.Name.Trim();
        if (name.Length == 0)
            throw new ValidationError("name", "Name is required.");
        var days = input.CreditTerms.Days() ?? input.CreditDays;
        if (days is < 0 or > 3650)
            throw new ValidationError("credit_days", "Credit days must be between 0 and 3650.");
        if (input.CreditLimit < 0)
            throw new ValidationError("credit_limit", "Ensure this value is greater than or equal to 0.");
        if (input.OpeningBalance < 0)
            throw new ValidationError("opening_balance", "Ensure this value is greater than or equal to 0.");
        if (input.OpeningSide is not ("DR" or "CR"))
            throw new ValidationError("opening_side", "Opening side must be DR or CR.");
        var email = input.Email.Trim();
        if (email.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(email, out _))
            throw new ValidationError("email", "Enter a valid email address.");
        return new Stored(name, input.PanVatNo.Trim(), input.Phone.Trim(), email, input.Address.Trim(),
            input.CreditTerms.ToCode(), days, Money.ToPaisa(input.CreditLimit), Money.ToPaisa(input.OpeningBalance),
            input.OpeningSide, input.Notes.Trim());
    }

    public static Party Map(Row r) => new()
    {
        Id = r.Long("id"),
        Type = PartyCodes.ParseType(r.Str("type")),
        Name = r.Str("name"),
        PanVatNo = r.Str("pan_vat_no"),
        Phone = r.Str("phone"),
        Email = r.Str("email"),
        Address = r.Str("address"),
        CreditTerms = PartyCodes.ParseTerms(r.Str("credit_terms")),
        CreditDays = (int)r.Long("credit_days"),
        CreditLimit = r.Money("credit_limit"),
        OpeningBalance = r.Money("opening_balance"),
        OpeningSide = r.Str("opening_side"),
        Notes = r.Str("notes"),
        IsActive = r.Bool("is_active"),
    };
}
