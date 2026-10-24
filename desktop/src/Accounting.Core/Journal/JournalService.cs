using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Products;
using Accounting.Core.Security;

namespace Accounting.Core.Journal;

public static class AccountGroup
{
    public sealed record Info(string Code, string Label, string Nature);

    public static readonly IReadOnlyList<Info> All =
    [
        new("CAPITAL", "Capital Account", "LIABILITY"),
        new("RESERVES", "Reserves & Surplus", "LIABILITY"),
        new("LOANS", "Loans (Liability)", "LIABILITY"),
        new("CURRENT_LIABILITIES", "Current Liabilities", "LIABILITY"),
        new("DUTIES_TAXES", "Duties & Taxes", "LIABILITY"),
        new("FIXED_ASSETS", "Fixed Assets", "ASSET"),
        new("INVESTMENTS", "Investments", "ASSET"),
        new("CURRENT_ASSETS", "Current Assets", "ASSET"),
        new("LOANS_ADVANCES", "Loans & Advances (Asset)", "ASSET"),
        new("DIRECT_INCOME", "Direct Income", "INCOME"),
        new("INDIRECT_INCOME", "Indirect Income", "INCOME"),
        new("DIRECT_EXPENSES", "Direct Expenses", "EXPENSE"),
        new("INDIRECT_EXPENSES", "Indirect Expenses", "EXPENSE"),
    ];

    public static Info Get(string code) =>
        All.FirstOrDefault(g => g.Code == code) ?? throw new ValidationError("group", $"\"{code}\" is not a valid choice.");

    /// <summary>Assets and expenses normally carry a debit balance; the rest a credit balance.</summary>
    public static string NormalSide(string code) => Get(code).Nature is "ASSET" or "EXPENSE" ? "DR" : "CR";
}

/// <summary>A signed balance shown as "1,000.00 Dr" / "Cr" (debit positive).</summary>
public readonly record struct DrCr(decimal Signed)
{
    public decimal Amount => Money.Round(Math.Abs(Signed));
    public string Side => Signed >= 0 ? "Dr" : "Cr";

    public override string ToString() => $"{Amount:N2} {Side}";
}

/// <summary>A ledger in the chart of accounts (capital, loans, fixed assets, ...).</summary>
public sealed record LedgerAccount(long Id, string Name, string Group, decimal OpeningBalance, string OpeningSide, string Notes, bool IsActive)
{
    public string GroupLabel => AccountGroup.Get(Group).Label;
    public string Nature => AccountGroup.Get(Group).Nature;
    public decimal SignedOpening => OpeningSide == "DR" ? OpeningBalance : -OpeningBalance;

    public override string ToString() => Name;
}

public sealed record LedgerInput(string Name, string Group, decimal OpeningBalance = 0, string? OpeningSide = null, string Notes = "");

/// <summary>
/// Where a journal line posts: "ledger:5", "cash:2", "party:9" or "expense:3".
/// </summary>
public sealed record AccountOption(string Key, string Name, string Group);

public sealed record JournalLine(int LineNo, AccountOption Account, decimal Debit, decimal Credit, string Narration);

public sealed record JournalVoucher
{
    public long Id { get; init; }
    public string Number { get; init; } = "";
    public DateOnly Date { get; init; }
    public string Narration { get; init; } = "";
    /// <summary>Total debit (= total credit).</summary>
    public decimal TotalAmount { get; init; }
    public string Status { get; init; } = DocumentStatus.Active;
    public DateOnly? CancelledOn { get; init; }
    public string CancelReason { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public IReadOnlyList<JournalLine> Lines { get; init; } = [];

    public bool IsCancelled => Status == DocumentStatus.Cancelled;
    public IEnumerable<string> DebitAccounts => Lines.Where(l => l.Debit > 0).Select(l => l.Account.Name);
    public IEnumerable<string> CreditAccounts => Lines.Where(l => l.Credit > 0).Select(l => l.Account.Name);

    public override string ToString() => Number;
}

public sealed record JournalLineInput(string Account, decimal Debit = 0, decimal Credit = 0, string Narration = "");

public sealed record LedgerBookEntry(DateOnly Date, long VoucherId, string Number, string Narration, decimal Debit, decimal Credit, DrCr Balance);

public sealed record LedgerBook(LedgerAccount Ledger, DrCr Opening, decimal TotalDebit, decimal TotalCredit, DrCr Closing,
    DateOnly? Start, DateOnly End, IReadOnlyList<LedgerBookEntry> Entries);

/// <summary>
/// Chart of accounts and journal vouchers (double-entry Dr/Cr adjustments), ported from
/// backend/apps/journal/services.py. Every voucher balances. Cancelled vouchers drop out of every
/// balance; nothing is deleted. Also supplies the journal amounts that party balances, cash books
/// and the expense report add in.
/// </summary>
public static class JournalService
{
    private const string LedgerModel = "journal.LedgerAccount";
    private const string VoucherModel = "journal.JournalVoucher";

    // ------------------------------------------------------------------ chart of accounts

    public static LedgerAccount CreateLedger(Tx tx, Actor actor, LedgerInput input)
    {
        Permissions.Require(actor, "journal.manage_ledgers");
        var d = ValidateLedger(tx, input, excludeId: null);
        var id = tx.Insert(
            "INSERT INTO ledger_accounts (name, \"group\", opening_balance, opening_side, notes) VALUES (@Name, @Group, @opening, @side, @Notes)",
            new { d.Name, d.Group, opening = Money.ToPaisa(d.OpeningBalance), side = d.OpeningSide, d.Notes });
        var ledger = GetLedger(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Create, LedgerModel, id, ledger.Name, after: AuditLog.Snapshot(ledger));
        return ledger;
    }

    public static LedgerAccount UpdateLedger(Tx tx, Actor actor, long id, LedgerInput input)
    {
        Permissions.Require(actor, "journal.manage_ledgers");
        var before = GetLedger(tx, id);
        var d = ValidateLedger(tx, input, excludeId: id);
        tx.Execute(
            """
            UPDATE ledger_accounts SET name = @Name, "group" = @Group, opening_balance = @opening, opening_side = @side, notes = @Notes,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @id
            """, new { id, d.Name, d.Group, opening = Money.ToPaisa(d.OpeningBalance), side = d.OpeningSide, d.Notes });
        var after = GetLedger(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Update, LedgerModel, id, after.Name, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    public static LedgerAccount SetLedgerActive(Tx tx, Actor actor, long id, bool active)
    {
        Permissions.Require(actor, "journal.manage_ledgers");
        var before = GetLedger(tx, id);
        tx.Execute("UPDATE ledger_accounts SET is_active = @active, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = @id", new { id, active });
        var after = GetLedger(tx, id);
        AuditLog.Record(tx, actor, active ? AuditAction.Activate : AuditAction.Deactivate, LedgerModel, id, after.Name,
            AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    public static void DeleteLedger(Tx tx, Actor actor, long id)
    {
        Permissions.Require(actor, "journal.manage_ledgers");
        var ledger = GetLedger(tx, id);
        if (tx.Scalar<long>("SELECT EXISTS (SELECT 1 FROM journal_lines WHERE ledger_id = @id)", new { id }) == 1)
            throw new BusinessError("This ledger has journal entries and cannot be deleted. Deactivate it instead.", "LEDGER_IN_USE", 409);
        AuditLog.Record(tx, actor, AuditAction.Delete, LedgerModel, id, ledger.Name, before: AuditLog.Snapshot(ledger));
        tx.Execute("DELETE FROM ledger_accounts WHERE id = @id", new { id });
    }

    private static LedgerAccount ValidateLedger(Tx tx, LedgerInput input, long? excludeId)
    {
        var name = input.Name.Trim();
        if (name.Length == 0)
            throw new ValidationError("name", "Name is required.");
        AccountGroup.Get(input.Group);
        if (input.OpeningBalance < 0)
            throw new ValidationError("opening_balance", "Ensure this value is greater than or equal to 0.");
        var side = input.OpeningSide ?? AccountGroup.NormalSide(input.Group);
        if (side is not ("DR" or "CR"))
            throw new ValidationError("opening_side", "Opening side must be DR or CR.");
        if (tx.Scalar<long>("SELECT COUNT(*) FROM ledger_accounts WHERE name = @name AND id IS NOT @excludeId", new { name, excludeId }) > 0)
            throw new BusinessError($"A ledger named {name} already exists.", "DUPLICATE_LEDGER");
        return new LedgerAccount(0, name, input.Group, Money.Round(input.OpeningBalance), side, input.Notes.Trim(), true);
    }

    public static LedgerAccount GetLedger(Tx tx, long id) =>
        tx.Rows("SELECT * FROM ledger_accounts WHERE id = @id", new { id }).Select(MapLedger).FirstOrDefault()
        ?? throw new NotFoundError("Ledger not found.");

    public static IReadOnlyList<LedgerAccount> Ledgers(Tx tx, bool? isActive = null, string? search = null) =>
        tx.Rows("SELECT * FROM ledger_accounts WHERE (@isActive IS NULL OR is_active = @isActive) AND (@search IS NULL OR name LIKE @search) ORDER BY name COLLATE NOCASE",
            new { isActive, search = ProductService.LikePattern(search) }).Select(MapLedger).ToList();

    private static LedgerAccount MapLedger(Row r) =>
        new(r.Long("id"), r.Str("name"), r.Str("group"), r.Money("opening_balance"), r.Str("opening_side"), r.Str("notes"), r.Bool("is_active"));

    private const string ActiveLines = "journal_lines l JOIN journal_vouchers v ON v.id = l.voucher_id AND v.status = 'ACTIVE'";

    /// <summary>Signed (Dr +) balance of every chart-of-accounts ledger, optionally as of a date.</summary>
    public static Dictionary<long, DrCr> LedgerBalances(Tx tx, DateOnly? asOf = null)
    {
        var result = Ledgers(tx).ToDictionary(l => l.Id, l => l.SignedOpening);
        foreach (var r in tx.Rows($"SELECT l.ledger_id, SUM(l.debit) - SUM(l.credit) AS net FROM {ActiveLines} WHERE l.ledger_id IS NOT NULL AND (@asOf IS NULL OR v.date <= @asOf) GROUP BY l.ledger_id",
                     new { asOf }))
            result[r.Long("ledger_id")] += r.Money("net");
        return result.ToDictionary(kv => kv.Key, kv => new DrCr(Money.Round(kv.Value)));
    }

    public static LedgerBook Book(Tx tx, long ledgerId, DateOnly? start = null, DateOnly? end = null)
    {
        var ledger = GetLedger(tx, ledgerId);
        var endDate = end ?? tx.Today;
        var opening = ledger.SignedOpening;
        if (start is { } s)
            opening += Money.FromPaisa(tx.Scalar<long>($"SELECT COALESCE(SUM(l.debit) - SUM(l.credit), 0) FROM {ActiveLines} WHERE l.ledger_id = @ledgerId AND v.date < @s",
                new { ledgerId, s }));
        var running = opening;
        decimal totalDr = 0, totalCr = 0;
        var entries = new List<LedgerBookEntry>();
        foreach (var r in tx.Rows(
                     $"""
                      SELECT l.*, v.date, v.number, v.narration AS voucher_narration FROM {ActiveLines}
                      WHERE l.ledger_id = @ledgerId AND (@start IS NULL OR v.date >= @start) AND v.date <= @end
                      ORDER BY v.date, v.id, l.line_no
                      """, new { ledgerId, start, end = endDate }))
        {
            decimal dr = r.Money("debit"), cr = r.Money("credit");
            running += dr - cr;
            totalDr += dr;
            totalCr += cr;
            var narration = r.Str("narration");
            entries.Add(new LedgerBookEntry(r.Date("date"), r.Long("voucher_id"), r.Str("number"),
                narration.Length > 0 ? narration : r.Str("voucher_narration"), dr, cr, new DrCr(Money.Round(running))));
        }
        return new LedgerBook(ledger, new DrCr(opening), totalDr, totalCr, new DrCr(Money.Round(running)), start, endDate, entries);
    }

    // ------------------------------------------------------------------ accounts a line can post to

    /// <summary>Ledgers, cash/bank accounts, customers, vendors and expense categories in one searchable list.</summary>
    public static IReadOnlyList<AccountOption> AccountOptions(Tx tx, string? search = null, int limit = 20)
    {
        var args = new { search = ProductService.LikePattern(search), limit };
        const string named = "is_active = 1 AND (@search IS NULL OR name LIKE @search) ORDER BY name COLLATE NOCASE LIMIT @limit";
        return
        [
            .. tx.Rows($"SELECT * FROM ledger_accounts WHERE {named}", args).Select(r => new AccountOption($"ledger:{r.Long("id")}", r.Str("name"), AccountGroup.Get(r.Str("group")).Label)),
            .. tx.Rows($"SELECT * FROM money_accounts WHERE {named}", args).Select(r => new AccountOption($"cash:{r.Long("id")}", r.Str("name"), $"Cash/Bank · {CashBank.AccountKind.Display(r.Str("kind"))}")),
            .. tx.Rows($"SELECT * FROM parties WHERE {named}", args).Select(r => new AccountOption($"party:{r.Long("id")}", r.Str("name"), r.Str("type") == "CUSTOMER" ? "Customer" : "Vendor")),
            .. tx.Rows($"SELECT * FROM expense_categories WHERE {named}", args).Select(r => new AccountOption($"expense:{r.Long("id")}", r.Str("name"), "Expense category")),
        ];
    }

    private static readonly Dictionary<string, (string Table, string Column, string Label)> Kinds = new()
    {
        ["ledger"] = ("ledger_accounts", "ledger_id", "Ledger"),
        ["cash"] = ("money_accounts", "money_account_id", "Cash/Bank"),
        ["party"] = ("parties", "party_id", "Party"),
        ["expense"] = ("expense_categories", "expense_category_id", "Expense category"),
    };

    /// <summary>"ledger:5" -> (column, id) after checking the account exists and is active.</summary>
    private static (string Column, long Id) Target(Tx tx, string? key)
    {
        var parts = (key ?? "").Split(':');
        if (parts.Length != 2 || !Kinds.TryGetValue(parts[0], out var kind) || !long.TryParse(parts[1], out var id))
            throw new BusinessError("Choose an account for every line.", "ACCOUNT_REQUIRED");
        var row = tx.Row($"SELECT name, is_active FROM {kind.Table} WHERE id = @id", new { id })
                  ?? throw new BusinessError($"{kind.Label} not found.", "ACCOUNT_NOT_FOUND");
        if (!row.Bool("is_active"))
            throw new BusinessError($"{row.Str("name")} is inactive.", "ACCOUNT_INACTIVE");
        return (kind.Column, id);
    }

    // ------------------------------------------------------------------ vouchers

    public static JournalVoucher CreateVoucher(Tx tx, Actor actor, DateOnly date, string narration, IReadOnlyList<JournalLineInput> lines)
    {
        Permissions.Require(actor, "journal.create");
        tx.RequireWrite();
        if (lines.Count < 2)
            throw new BusinessError("A journal voucher needs at least two lines.", "TOO_FEW_LINES");
        var prepared = new List<(int No, string Column, long Id, decimal Dr, decimal Cr, string Narration)>();
        decimal totalDr = 0, totalCr = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            decimal dr = Money.Round(lines[i].Debit), cr = Money.Round(lines[i].Credit);
            if (dr < 0 || cr < 0 || dr > 0 == cr > 0)
                throw new BusinessError($"Line {i + 1}: enter either a debit or a credit amount.", "INVALID_LINE");
            var (column, id) = Target(tx, lines[i].Account);
            prepared.Add((i + 1, column, id, dr, cr, lines[i].Narration.Trim()));
            totalDr += dr;
            totalCr += cr;
        }
        if (totalDr != totalCr)
            throw new BusinessError($"Debit total ({totalDr:N2}) and credit total ({totalCr:N2}) must be equal.", "NOT_BALANCED");
        var voucherId = tx.Insert(
            "INSERT INTO journal_vouchers (number, date, narration, total_amount, created_by) VALUES (@number, @date, @narration, @total, @UserId)",
            new { number = Sequences.Next(tx, Sequences.Journal), date, narration = narration.Trim(), total = Money.ToPaisa(totalDr), actor.UserId });
        foreach (var l in prepared)
            tx.Execute($"INSERT INTO journal_lines (voucher_id, line_no, {l.Column}, debit, credit, narration) VALUES (@voucherId, @no, @id, @dr, @cr, @narration)",
                new { voucherId, no = l.No, id = l.Id, dr = Money.ToPaisa(l.Dr), cr = Money.ToPaisa(l.Cr), narration = l.Narration });
        var voucher = GetVoucher(tx, voucherId);
        AuditLog.Record(tx, actor, AuditAction.Create, VoucherModel, voucherId, voucher.Number, after: AuditLog.Snapshot(voucher));
        return voucher;
    }

    public static JournalVoucher CancelVoucher(Tx tx, Actor actor, long id, string reason)
    {
        Permissions.Require(actor, "journal.cancel");
        var before = GetVoucher(tx, id);
        if (before.IsCancelled)
            throw new BusinessError($"{before.Number} is already cancelled.", "ALREADY_CANCELLED");
        if (string.IsNullOrWhiteSpace(reason))
            throw new BusinessError("A cancellation reason is required.", "REASON_REQUIRED");
        tx.Execute(
            """
            UPDATE journal_vouchers SET status = 'CANCELLED', cancelled_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), cancelled_on = @today,
                cancelled_by = @UserId, cancel_reason = @reason, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @id
            """, new { id, today = tx.Today, actor.UserId, reason = reason.Trim() });
        var after = GetVoucher(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Cancel, VoucherModel, id, after.Number, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    public static JournalVoucher GetVoucher(Tx tx, long id)
    {
        var voucher = tx.Rows("SELECT * FROM journal_vouchers WHERE id = @id", new { id }).Select(MapVoucher).FirstOrDefault()
                      ?? throw new NotFoundError("Journal voucher not found.");
        return voucher with { Lines = VoucherLines(tx, id) };
    }

    private static IReadOnlyList<JournalLine> VoucherLines(Tx tx, long voucherId) =>
        tx.Rows(
            """
            SELECT l.*, lg.name AS ledger_name, lg."group" AS ledger_group, m.name AS money_name, m.kind AS money_kind,
                   p.name AS party_name, p.type AS party_type, c.name AS category_name
            FROM journal_lines l
            LEFT JOIN ledger_accounts lg ON lg.id = l.ledger_id
            LEFT JOIN money_accounts m ON m.id = l.money_account_id
            LEFT JOIN parties p ON p.id = l.party_id
            LEFT JOIN expense_categories c ON c.id = l.expense_category_id
            WHERE l.voucher_id = @voucherId ORDER BY l.line_no
            """, new { voucherId })
            .Select(r => new JournalLine((int)r.Long("line_no"), LineAccount(r), r.Money("debit"), r.Money("credit"), r.Str("narration")))
            .ToList();

    private static AccountOption LineAccount(Row r) =>
        r.NullableLong("ledger_id") is { } l ? new($"ledger:{l}", r.Str("ledger_name"), AccountGroup.Get(r.Str("ledger_group")).Label)
        : r.NullableLong("money_account_id") is { } m ? new($"cash:{m}", r.Str("money_name"), $"Cash/Bank · {CashBank.AccountKind.Display(r.Str("money_kind"))}")
        : r.NullableLong("party_id") is { } p ? new($"party:{p}", r.Str("party_name"), r.Str("party_type") == "CUSTOMER" ? "Customer" : "Vendor")
        : new($"expense:{r.Long("expense_category_id")}", r.Str("category_name"), "Expense category");

    public static Page<JournalVoucher> ListVouchers(Tx tx, DateOnly? start = null, DateOnly? end = null, string? status = null, string? search = null, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        const string where =
            """
            WHERE (@start IS NULL OR date >= @start) AND (@end IS NULL OR date <= @end) AND (@status IS NULL OR status = @status)
              AND (@search IS NULL OR number LIKE @search OR narration LIKE @search)
            """;
        var args = new { start, end, status, search = ProductService.LikePattern(search), limit = page.Size, offset = page.Offset };
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM journal_vouchers {where}", args);
        var rows = tx.Rows($"SELECT * FROM journal_vouchers {where} ORDER BY date DESC, id DESC LIMIT @limit OFFSET @offset", args)
            .Select(MapVoucher).Select(v => v with { Lines = VoucherLines(tx, v.Id) }).ToList();
        return new Page<JournalVoucher>(rows, (int)count, page.Page, page.Size);
    }

    private static JournalVoucher MapVoucher(Row r) => new()
    {
        Id = r.Long("id"),
        Number = r.Str("number"),
        Date = r.Date("date"),
        Narration = r.Str("narration"),
        TotalAmount = r.Money("total_amount"),
        Status = r.Str("status"),
        CancelledOn = r.NullableDate("cancelled_on"),
        CancelReason = r.Str("cancel_reason"),
        CreatedAt = r.Timestamp("created_at"),
    };

    // ------------------------------------------------------------------ amounts other modules add in

    /// <summary>
    /// Net change in what each party owes (receivable) or is owed (payable) from journal lines.
    /// Customers: a debit increases what they owe. Vendors: a credit increases what we owe.
    /// </summary>
    public static Dictionary<long, decimal> PartyEffect(Tx tx, AccountConfig cfg, DateOnly? asOf = null, long? partyId = null)
    {
        var sign = cfg.DocumentIsDebit ? 1 : -1;
        return tx.Rows(
                $"""
                 SELECT l.party_id, SUM(l.debit) - SUM(l.credit) AS net FROM {ActiveLines} JOIN parties p ON p.id = l.party_id
                 WHERE p.type = @type AND (@partyId IS NULL OR l.party_id = @partyId) AND (@asOf IS NULL OR v.date <= @asOf)
                 GROUP BY l.party_id
                 """, new { type = cfg.PartyType.ToCode(), partyId, asOf })
            .ToDictionary(r => r.Long("party_id"), r => Money.Round(sign * r.Money("net")));
    }

    /// <summary>Journal lines of one party as ledger entries (with their effect on the amount owed).</summary>
    public static IEnumerable<PartyLedgers.Pending> PartyLines(Tx tx, AccountConfig cfg, long partyId, DateOnly? start, DateOnly end) =>
        tx.Rows(
                $"""
                 SELECT l.*, v.date, v.number, v.narration AS voucher_narration, v.status, v.created_at AS voucher_created
                 FROM {ActiveLines} WHERE l.party_id = @partyId AND v.date <= @end AND (@start IS NULL OR v.date >= @start)
                 """, new { partyId, start, end })
            .Select(r =>
            {
                decimal dr = r.Money("debit"), cr = r.Money("credit");
                var narration = r.Str("narration").Length > 0 ? r.Str("narration")
                    : r.Str("voucher_narration").Length > 0 ? r.Str("voucher_narration") : "Journal voucher";
                var entry = new LedgerEntry(r.Date("date"), r.Str("number"), r.Long("voucher_id"), "journal", "Journal", narration,
                    dr, cr, null, null, r.Str("status"), 0m);
                return new PartyLedgers.Pending(entry, cfg.DocumentIsDebit ? dr - cr : cr - dr, r.Str("voucher_created"));
            });

    /// <summary>Net debit posted to each expense category by journal vouchers in the period.</summary>
    public static Dictionary<long, decimal> ExpenseCategoryTotals(Tx tx, DateOnly? start = null, DateOnly? end = null, long? categoryId = null) =>
        tx.Rows(
                $"""
                 SELECT l.expense_category_id, SUM(l.debit) - SUM(l.credit) AS net FROM {ActiveLines}
                 WHERE l.expense_category_id IS NOT NULL AND (@start IS NULL OR v.date >= @start) AND (@end IS NULL OR v.date <= @end)
                   AND (@categoryId IS NULL OR l.expense_category_id = @categoryId)
                 GROUP BY l.expense_category_id
                 """, new { start, end, categoryId })
            .ToDictionary(r => r.Long("expense_category_id"), r => Money.Round(r.Money("net")));
}
