using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Products;
using Accounting.Core.Security;

namespace Accounting.Core.CashBank;

/// <summary>Money moved from one of the business's accounts to another (CNTR-000001), e.g. cash deposited in the bank.</summary>
public sealed record Contra(
    long Id, string Number, DateOnly Date, long FromAccountId, string FromAccountName, long ToAccountId, string ToAccountName,
    decimal Amount, string ReferenceNumber, string Notes, string Status, DateOnly? CancelledOn, string CancelReason)
{
    public bool IsCancelled => Status == DocumentStatus.Cancelled;

    public override string ToString() => Number;
}

/// <summary>One line of a cash/bank book.</summary>
public sealed record BookEntry(
    DateOnly Date, string VoucherType, string Number, long ObjectId, string Particulars, string ReferenceNumber,
    decimal MoneyIn, decimal MoneyOut, decimal Balance);

public sealed record AccountBook(MoneyAccount Account, decimal OpeningBalance, decimal TotalIn, decimal TotalOut, decimal ClosingBalance,
    DateOnly? Start, DateOnly End, IReadOnlyList<BookEntry> Entries);

/// <summary>One voucher of any kind in the day book.</summary>
public sealed record DayBookEntry(
    DateOnly Date, string VoucherType, string Number, long ObjectId, string Link, string Particulars, string AccountName,
    decimal Amount, string Status);

public sealed record DayBook(DateOnly Start, DateOnly End, IReadOnlyList<DayBookEntry> Entries, IReadOnlyList<(string VoucherType, decimal Amount)> Totals);

/// <summary>
/// Cash/bank books, contra entries and the day book, ported from backend/apps/cashbank/services.py.
/// Every active receipt, vendor payment, expense, contra and cash/bank journal line moves money in or
/// out of one account; cancelled ones simply drop out of the book.
/// </summary>
public static class CashBook
{
    private const string ContraModel = "cashbank.Contra";

    // Every money movement: account, direction, and what to show in the book. "ord" breaks ties within a date.
    private const string Moves =
        """
        WITH moves AS (
            SELECT r.account_id, r.date, 'Receipt' AS voucher_type, r.number, r.id AS object_id, p.name AS particulars,
                   r.reference_number, r.amount AS money_in, 0 AS money_out, r.created_at AS ord
            FROM customer_receipts r JOIN parties p ON p.id = r.party_id WHERE r.status = 'ACTIVE'
            UNION ALL
            SELECT r.account_id, r.date, 'Payment', r.number, r.id, p.name, r.reference_number, 0, r.amount, r.created_at
            FROM vendor_payments r JOIN parties p ON p.id = r.party_id WHERE r.status = 'ACTIVE'
            UNION ALL
            SELECT e.account_id, e.date, 'Expense', e.number, e.id,
                   c.name || ': ' || e.description
                       || CASE WHEN COALESCE(v.name, e.payee) <> '' THEN ' (' || COALESCE(v.name, e.payee) || ')' ELSE '' END,
                   e.reference_number, 0, e.total_amount, e.created_at
            FROM expenses e JOIN expense_categories c ON c.id = e.category_id LEFT JOIN parties v ON v.id = e.vendor_id
            WHERE e.status = 'ACTIVE'
            UNION ALL
            SELECT x.to_account_id, x.date, 'Contra', x.number, x.id, 'Transfer from ' || f.name, x.reference_number, x.amount, 0, x.created_at
            FROM contras x JOIN money_accounts f ON f.id = x.from_account_id WHERE x.status = 'ACTIVE'
            UNION ALL
            SELECT x.from_account_id, x.date, 'Contra', x.number, x.id, 'Transfer to ' || t.name, x.reference_number, 0, x.amount, x.created_at
            FROM contras x JOIN money_accounts t ON t.id = x.to_account_id WHERE x.status = 'ACTIVE'
            UNION ALL
            SELECT l.money_account_id, v.date, 'Journal', v.number, v.id,
                   COALESCE(NULLIF(l.narration, ''), NULLIF(v.narration, ''), 'Journal voucher'), '', l.debit, l.credit,
                   v.created_at || printf('%04d', l.line_no)
            FROM journal_lines l JOIN journal_vouchers v ON v.id = l.voucher_id
            WHERE v.status = 'ACTIVE' AND l.money_account_id IS NOT NULL
        )
        """;

    /// <summary>Closing balance of the account at the end of <paramref name="asOf"/> (all time when null).</summary>
    public static decimal Balance(Tx tx, long accountId, DateOnly? asOf = null)
    {
        var account = MoneyAccounts.Get(tx, accountId);
        var net = tx.Scalar<long>($"{Moves} SELECT COALESCE(SUM(money_in - money_out), 0) FROM moves WHERE account_id = @accountId AND (@asOf IS NULL OR date <= @asOf)",
            new { accountId, asOf });
        return Money.Round(account.OpeningBalance + Money.FromPaisa(net));
    }

    /// <summary>Current balance of every account.</summary>
    public static Dictionary<long, decimal> Balances(Tx tx)
    {
        var result = MoneyAccounts.List(tx).ToDictionary(a => a.Id, a => a.OpeningBalance);
        foreach (var r in tx.Rows($"{Moves} SELECT account_id, SUM(money_in - money_out) AS net FROM moves GROUP BY account_id"))
            result[r.Long("account_id")] += r.Money("net");
        return result.ToDictionary(kv => kv.Key, kv => Money.Round(kv.Value));
    }

    /// <summary>The account's cash/bank book. A period starts from the balance carried forward.</summary>
    public static AccountBook Book(Tx tx, long accountId, DateOnly? start = null, DateOnly? end = null)
    {
        var account = MoneyAccounts.Get(tx, accountId);
        var endDate = end ?? tx.Today;
        var opening = start is { } s ? Balance(tx, accountId, s.AddDays(-1)) : account.OpeningBalance;
        var running = opening;
        decimal totalIn = 0, totalOut = 0;
        var entries = new List<BookEntry>();
        foreach (var r in tx.Rows(
                     $"{Moves} SELECT * FROM moves WHERE account_id = @accountId AND date <= @end AND (@start IS NULL OR date >= @start) ORDER BY date, ord",
                     new { accountId, start, end = endDate }))
        {
            decimal moneyIn = r.Money("money_in"), moneyOut = r.Money("money_out");
            totalIn += moneyIn;
            totalOut += moneyOut;
            running += moneyIn - moneyOut;
            entries.Add(new BookEntry(r.Date("date"), r.Str("voucher_type"), r.Str("number"), r.Long("object_id"), r.Str("particulars"),
                r.Str("reference_number"), moneyIn, moneyOut, Money.Round(running)));
        }
        return new AccountBook(account, Money.Round(opening), Money.Round(totalIn), Money.Round(totalOut), Money.Round(running), start, endDate, entries);
    }

    /// <summary>True when any receipt, payment, expense, contra or journal line uses the account.</summary>
    internal static bool IsUsed(Tx tx, long accountId) =>
        tx.Scalar<long>(
            """
            SELECT EXISTS (SELECT 1 FROM customer_receipts WHERE account_id = @accountId)
                OR EXISTS (SELECT 1 FROM vendor_payments WHERE account_id = @accountId)
                OR EXISTS (SELECT 1 FROM expenses WHERE account_id = @accountId)
                OR EXISTS (SELECT 1 FROM contras WHERE from_account_id = @accountId OR to_account_id = @accountId)
                OR EXISTS (SELECT 1 FROM journal_lines WHERE money_account_id = @accountId)
            """, new { accountId }) == 1;

    // ------------------------------------------------------------------ contra entries

    public static Contra CreateContra(Tx tx, Actor actor, DateOnly date, long fromAccountId, long toAccountId, decimal amount,
        string referenceNumber = "", string notes = "")
    {
        Permissions.Require(actor, "contra.create");
        tx.RequireWrite();
        var source = MoneyAccounts.Get(tx, fromAccountId);
        var target = MoneyAccounts.Get(tx, toAccountId);
        if (source.Id == target.Id)
            throw new BusinessError("Choose two different accounts.", "SAME_ACCOUNT");
        foreach (var account in new[] { source, target })
            if (!account.IsActive)
                throw new BusinessError($"Account {account.Name} is inactive.", "ACCOUNT_INACTIVE");
        amount = Money.Round(amount);
        if (amount <= 0)
            throw new BusinessError("Amount must be greater than zero.", "INVALID_AMOUNT");
        var id = tx.Insert(
            """
            INSERT INTO contras (number, date, from_account_id, to_account_id, amount, reference_number, notes, created_by)
            VALUES (@number, @date, @fromAccountId, @toAccountId, @amount, @referenceNumber, @notes, @UserId)
            """, new
            {
                number = Sequences.Next(tx, Sequences.Contra), date, fromAccountId, toAccountId, amount = Money.ToPaisa(amount),
                referenceNumber = referenceNumber.Trim(), notes = notes.Trim(), actor.UserId,
            });
        var contra = GetContra(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Create, ContraModel, id, contra.Number, after: AuditLog.Snapshot(contra));
        return contra;
    }

    public static Contra CancelContra(Tx tx, Actor actor, long id, string reason)
    {
        Permissions.Require(actor, "contra.cancel");
        var before = GetContra(tx, id);
        if (before.IsCancelled)
            throw new BusinessError($"{before.Number} is already cancelled.", "ALREADY_CANCELLED");
        if (string.IsNullOrWhiteSpace(reason))
            throw new BusinessError("A cancellation reason is required.", "REASON_REQUIRED");
        tx.Execute(
            """
            UPDATE contras SET status = 'CANCELLED', cancelled_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), cancelled_on = @today,
                cancelled_by = @UserId, cancel_reason = @reason, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @id
            """, new { id, today = tx.Today, actor.UserId, reason = reason.Trim() });
        var after = GetContra(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Cancel, ContraModel, id, after.Number, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    private const string ContraSelect =
        """
        SELECT x.*, f.name AS from_name, t.name AS to_name FROM contras x
        JOIN money_accounts f ON f.id = x.from_account_id JOIN money_accounts t ON t.id = x.to_account_id
        """;

    public static Contra GetContra(Tx tx, long id) =>
        tx.Rows(ContraSelect + " WHERE x.id = @id", new { id }).Select(MapContra).FirstOrDefault() ?? throw new NotFoundError("Contra entry not found.");

    public static Page<Contra> ListContras(Tx tx, DateOnly? start = null, DateOnly? end = null, long? accountId = null, string? status = null, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        const string where =
            """
            WHERE (@start IS NULL OR x.date >= @start) AND (@end IS NULL OR x.date <= @end) AND (@status IS NULL OR x.status = @status)
              AND (@accountId IS NULL OR x.from_account_id = @accountId OR x.to_account_id = @accountId)
            """;
        var args = new { start, end, accountId, status, limit = page.Size, offset = page.Offset };
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM contras x {where}", args);
        var rows = tx.Rows($"{ContraSelect} {where} ORDER BY x.date DESC, x.id DESC LIMIT @limit OFFSET @offset", args).Select(MapContra).ToList();
        return new Page<Contra>(rows, (int)count, page.Page, page.Size);
    }

    private static Contra MapContra(Row r) => new(
        r.Long("id"), r.Str("number"), r.Date("date"), r.Long("from_account_id"), r.Str("from_name"), r.Long("to_account_id"), r.Str("to_name"),
        r.Money("amount"), r.Str("reference_number"), r.Str("notes"), r.Str("status"), r.NullableDate("cancelled_on"), r.Str("cancel_reason"));

    // ------------------------------------------------------------------ day book

    private const string DayBookSql =
        """
        SELECT * FROM (
            SELECT d.date, 'Sales' AS voucher_type, d.number, d.id AS object_id, 'sales' AS link, p.name AS particulars, '' AS account_name,
                   d.total_amount AS amount, d.status, d.created_at AS ord
            FROM sales d JOIN parties p ON p.id = d.party_id
            UNION ALL
            SELECT d.date, 'Purchase', d.number, d.id, 'purchases', p.name, '', d.total_amount, d.status, d.created_at
            FROM purchases d JOIN parties p ON p.id = d.party_id
            UNION ALL
            SELECT r.date, 'Receipt', r.number, r.id, 'receipts', p.name, a.name, r.amount, r.status, r.created_at
            FROM customer_receipts r JOIN parties p ON p.id = r.party_id JOIN money_accounts a ON a.id = r.account_id
            UNION ALL
            SELECT r.date, 'Payment', r.number, r.id, 'payments', p.name, a.name, r.amount, r.status, r.created_at
            FROM vendor_payments r JOIN parties p ON p.id = r.party_id JOIN money_accounts a ON a.id = r.account_id
            UNION ALL
            SELECT e.date, 'Expense', e.number, e.id, 'expenses',
                   c.name || ': ' || e.description
                       || CASE WHEN COALESCE(v.name, e.payee) <> '' THEN ' (' || COALESCE(v.name, e.payee) || ')' ELSE '' END,
                   a.name, e.total_amount, e.status, e.created_at
            FROM expenses e JOIN expense_categories c ON c.id = e.category_id JOIN money_accounts a ON a.id = e.account_id
            LEFT JOIN parties v ON v.id = e.vendor_id
            UNION ALL
            SELECT x.date, 'Contra', x.number, x.id, 'cash-bank/contra', COALESCE(NULLIF(x.notes, ''), 'Transfer between accounts'),
                   f.name || ' → ' || t.name, x.amount, x.status, x.created_at
            FROM contras x JOIN money_accounts f ON f.id = x.from_account_id JOIN money_accounts t ON t.id = x.to_account_id
            UNION ALL
            SELECT j.date, 'Journal', j.number, j.id, 'journal', COALESCE(NULLIF(j.narration, ''), 'Journal voucher'), '',
                   j.total_amount, j.status, j.created_at
            FROM journal_vouchers j
        )
        WHERE date >= @start AND date <= @end AND (@includeCancelled OR status = 'ACTIVE')
        ORDER BY date, ord
        """;

    /// <summary>Every voucher dated between the two dates, in date order, with totals per voucher type (active only).</summary>
    public static DayBook DayBook(Tx tx, DateOnly start, DateOnly end, bool includeCancelled = false)
    {
        var entries = tx.Rows(DayBookSql, new { start, end, includeCancelled })
            .Select(r => new DayBookEntry(r.Date("date"), r.Str("voucher_type"), r.Str("number"), r.Long("object_id"), r.Str("link"),
                r.Str("particulars"), r.Str("account_name"), r.Money("amount"), r.Str("status")))
            .ToList();
        var totals = entries.Where(e => e.Status == DocumentStatus.Active).GroupBy(e => e.VoucherType)
            .Select(g => (g.Key, Money.Round(g.Sum(e => e.Amount)))).ToList();
        return new DayBook(start, end, entries, totals);
    }
}
