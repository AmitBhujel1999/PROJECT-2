using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Journal;
using Accounting.Core.Parties;
using Accounting.Core.Products;

namespace Accounting.Core.Payments;

public static class AgingBucket
{
    public const string Current = "current";
    public const string Days1To30 = "days_1_30";
    public const string Days31To60 = "days_31_60";
    public const string Days61To90 = "days_61_90";
    public const string Days91To120 = "days_91_120";
    public const string Over120 = "days_over_120";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Current, "Current / Not Due"),
        (Days1To30, "1-30 Days"),
        (Days31To60, "31-60 Days"),
        (Days61To90, "61-90 Days"),
        (Days91To120, "91-120 Days"),
        (Over120, "120+ Days"),
    ];

    public static string For(int daysOverdue) => daysOverdue switch
    {
        <= 0 => Current,
        <= 30 => Days1To30,
        <= 60 => Days31To60,
        <= 90 => Days61To90,
        <= 120 => Days91To120,
        _ => Over120,
    };
}

/// <summary>Amount per aging bucket plus the balance parts. NetBalance = Total - Unallocated + Journal + Opening.</summary>
public sealed record AgingAmounts
{
    public IReadOnlyDictionary<string, decimal> Buckets { get; init; } = AgingBucket.All.ToDictionary(b => b.Key, _ => 0m);
    public decimal Total { get; init; }
    public decimal Unallocated { get; init; }
    public decimal Journal { get; init; }
    public decimal Opening { get; init; }
    public decimal NetBalance => Money.Round(Total - Unallocated + Journal + Opening);

    public decimal this[string bucket] => Buckets[bucket];

    public static AgingAmounts Sum(IEnumerable<AgingAmounts> rows)
    {
        var list = rows.ToList();
        return new AgingAmounts
        {
            Buckets = AgingBucket.All.ToDictionary(b => b.Key, b => Money.Round(list.Sum(r => r[b.Key]))),
            Total = Money.Round(list.Sum(r => r.Total)),
            Unallocated = Money.Round(list.Sum(r => r.Unallocated)),
            Journal = Money.Round(list.Sum(r => r.Journal)),
            Opening = Money.Round(list.Sum(r => r.Opening)),
        };
    }
}

public sealed record AgingRow(long PartyId, string PartyName, string Phone, string PanVatNo, decimal CreditLimit, int DocumentCount, AgingAmounts Amounts);

public sealed record AgingSummary(DateOnly AsOf, IReadOnlyList<AgingRow> Rows, AgingAmounts Totals);

public sealed record AgingDocument(long Id, string Number, DateOnly Date, DateOnly DueDate, decimal TotalAmount, decimal PaidAsOf,
    decimal Outstanding, int DaysOverdue, string Bucket);

public sealed record UnallocatedPayment(long Id, string Number, DateOnly Date, decimal Amount, decimal Unallocated);

public sealed record PartyAgingDetail(DateOnly AsOf, AgingAmounts Totals, IReadOnlyList<AgingDocument> Documents, IReadOnlyList<UnallocatedPayment> UnallocatedPayments);

/// <summary>One line of a customer or vendor ledger. Balance = amount owed after this line.</summary>
public sealed record LedgerEntry(
    DateOnly Date, string Reference, long ReferenceId, string ReferenceType, string TransactionType, string Description,
    decimal Debit, decimal Credit, DateOnly? DueDate, string? PaymentStatus, string Status, decimal Balance);

public sealed record PartyLedger(decimal OpeningBalance, decimal ClosingBalance, decimal TotalDebit, decimal TotalCredit,
    DateOnly? Start, DateOnly End, IReadOnlyList<LedgerEntry> Entries);

public sealed record PartySummary(
    long PartyId, decimal TotalDocuments, int DocumentCount, decimal TotalPayments, int PaymentCount, decimal Outstanding,
    decimal Overdue, decimal Current, decimal Unallocated, decimal Journal, decimal Opening, decimal NetBalance,
    DateOnly? LastTransactionDate, decimal CreditLimit, decimal? AvailableCredit);

public sealed record AccountsOverview(decimal Outstanding, decimal Overdue, decimal Unallocated, decimal Journal, decimal Opening);

/// <summary>
/// Read side of the receivable / payable engine: balances on any date, party ledgers
/// (statements), aging and summaries, ported from party_accounts.py. Everything is derived
/// from documents, payments and allocations; nothing reads a stored balance.
///
/// Sign convention: a positive balance is always "owed on the account" — the customer
/// owes us (receivable) or we owe the vendor (payable).
/// Customer ledger: invoice = Debit, receipt = Credit. Vendor ledger: bill = Credit, payment = Debit.
/// </summary>
public static class PartyLedgers
{
    // ------------------------------------------------------------------ opening & journal

    /// <summary>Opening balances in "amount owed" terms, per party with a non-zero opening.</summary>
    public static Dictionary<long, decimal> Openings(Tx tx, AccountConfig cfg, long? partyId = null)
    {
        var sign = cfg.DocumentIsDebit ? 1 : -1;
        return tx.Rows("SELECT * FROM parties WHERE type = @type AND opening_balance > 0 AND (@partyId IS NULL OR id = @partyId)",
                new { type = cfg.PartyType.ToCode(), partyId })
            .Select(PartyService.Map).ToDictionary(p => p.Id, p => Money.Round(sign * p.SignedOpening));
    }

    /// <summary>Journal-voucher adjustments to what each party owes.</summary>
    public static Dictionary<long, decimal> JournalEffect(Tx tx, AccountConfig cfg, DateOnly? asOf = null, long? partyId = null) =>
        JournalService.PartyEffect(tx, cfg, asOf, partyId);

    // ------------------------------------------------------------------ as-of views

    private sealed record DocAsOf(long Id, long PartyId, string Number, DateOnly Date, DateOnly DueDate, decimal Total, decimal Allocated)
    {
        public decimal Outstanding => Total - Allocated;
    }

    private sealed record PayAsOf(long Id, long PartyId, string Number, DateOnly Date, decimal Amount, decimal Allocated)
    {
        public decimal Unallocated => Amount - Allocated;
    }

    // An allocation counts at T when it is effective by T and was not voided by T.
    private const string AllocatedAt =
        "SELECT COALESCE(SUM(a.amount), 0) FROM {0} a WHERE a.{1} = x.id AND a.date <= @asOf AND (a.is_active = 1 OR a.voided_on > @asOf)";

    // Existed at T and not yet cancelled at T.
    private const string LiveAt = "x.date <= @asOf AND NOT (x.status = 'CANCELLED' AND x.cancelled_on <= @asOf) AND (@partyId IS NULL OR x.party_id = @partyId)";

    private static List<DocAsOf> DocsAsOf(Tx tx, AccountConfig cfg, DateOnly asOf, long? partyId) =>
        tx.Rows($"""
                 SELECT x.id, x.party_id, x.number, x.date, x.due_date, x.total_amount,
                        ({string.Format(AllocatedAt, cfg.AllocationTable, "document_id")}) AS allocated
                 FROM {cfg.DocTable} x WHERE {LiveAt}
                 ORDER BY x.due_date, x.date, x.id
                 """, new { asOf, partyId })
            .Select(r => new DocAsOf(r.Long("id"), r.Long("party_id"), r.Str("number"), r.Date("date"), r.Date("due_date"),
                r.Money("total_amount"), r.Money("allocated")))
            .ToList();

    private static List<PayAsOf> PaymentsAsOf(Tx tx, AccountConfig cfg, DateOnly asOf, long? partyId) =>
        tx.Rows($"""
                 SELECT x.id, x.party_id, x.number, x.date, x.amount,
                        ({string.Format(AllocatedAt, cfg.AllocationTable, "payment_id")}) AS allocated
                 FROM {cfg.PaymentTable} x WHERE {LiveAt}
                 ORDER BY x.date, x.id
                 """, new { asOf, partyId })
            .Select(r => new PayAsOf(r.Long("id"), r.Long("party_id"), r.Str("number"), r.Date("date"), r.Money("amount"), r.Money("allocated")))
            .ToList();

    /// <summary>Documents minus payments recognised up to <paramref name="asOf"/> (cancellations reverse on their date), plus journal and opening.</summary>
    public static decimal BalanceAsOf(Tx tx, AccountConfig cfg, DateOnly asOf, long? partyId = null)
    {
        const string sums =
            """
            SELECT COALESCE(SUM(CASE WHEN date <= @asOf THEN {0} END), 0)
                 - COALESCE(SUM(CASE WHEN date <= @asOf AND status = 'CANCELLED' AND cancelled_on <= @asOf THEN {0} END), 0)
            FROM {1} WHERE @partyId IS NULL OR party_id = @partyId
            """;
        var docs = tx.Scalar<long>(string.Format(sums, "total_amount", cfg.DocTable), new { asOf, partyId });
        var pays = tx.Scalar<long>(string.Format(sums, "amount", cfg.PaymentTable), new { asOf, partyId });
        var journal = JournalEffect(tx, cfg, asOf, partyId).Values.Sum();
        var opening = Openings(tx, cfg, partyId).Values.Sum();
        return Money.Round(Money.FromPaisa(docs - pays) + journal + opening);
    }

    // ------------------------------------------------------------------ aging

    /// <summary>
    /// Per-party aging buckets from due dates at <paramref name="asOf"/>. Only the outstanding part of each
    /// (partly paid) document is aged. Advances are reported separately and never counted as overdue.
    /// </summary>
    public static AgingSummary Aging(Tx tx, AccountConfig cfg, DateOnly asOf, long? partyId = null, string? search = null)
    {
        var docs = DocsAsOf(tx, cfg, asOf, partyId).Where(d => d.Outstanding > 0).ToLookup(d => d.PartyId);
        var unallocated = PaymentsAsOf(tx, cfg, asOf, partyId).Where(p => p.Unallocated > 0)
            .GroupBy(p => p.PartyId).ToDictionary(g => g.Key, g => g.Sum(p => p.Unallocated));
        var journal = JournalEffect(tx, cfg, asOf, partyId).Where(j => j.Value != 0).ToDictionary();
        var opening = Openings(tx, cfg, partyId);
        var ids = docs.Select(g => g.Key).Concat(unallocated.Keys).Concat(journal.Keys).Concat(opening.Keys).ToHashSet();
        var parties = tx.Rows(
                """
                SELECT * FROM parties WHERE type = @type
                  AND (@search IS NULL OR name LIKE @search OR phone LIKE @search OR pan_vat_no LIKE @search)
                ORDER BY name COLLATE NOCASE, id
                """, new { type = cfg.PartyType.ToCode(), search = ProductService.LikePattern(search) })
            .Select(PartyService.Map).Where(p => ids.Contains(p.Id));
        var rows = parties.Select(p =>
        {
            var partyDocs = docs[p.Id].ToList();
            return new AgingRow(p.Id, p.Name, p.Phone, p.PanVatNo, p.CreditLimit, partyDocs.Count, new AgingAmounts
            {
                Buckets = Bucketise(partyDocs.Select(d => (asOf.DayNumber - d.DueDate.DayNumber, d.Outstanding))),
                Total = Money.Round(partyDocs.Sum(d => d.Outstanding)),
                Unallocated = Money.Round(unallocated.GetValueOrDefault(p.Id)),
                Journal = Money.Round(journal.GetValueOrDefault(p.Id)),
                Opening = Money.Round(opening.GetValueOrDefault(p.Id)),
            });
        }).ToList();
        return new AgingSummary(asOf, rows, AgingAmounts.Sum(rows.Select(r => r.Amounts)));
    }

    private static Dictionary<string, decimal> Bucketise(IEnumerable<(int DaysOverdue, decimal Amount)> items)
    {
        var buckets = AgingBucket.All.ToDictionary(b => b.Key, _ => 0m);
        foreach (var (days, amount) in items)
            buckets[AgingBucket.For(days)] += amount;
        return buckets.ToDictionary(b => b.Key, b => Money.Round(b.Value));
    }

    /// <summary>Open documents at <paramref name="asOf"/> across parties (receivables / payables report), oldest due first.</summary>
    public static IReadOnlyList<(AgingDocument Document, long PartyId, string PartyName)> OutstandingDocuments(
        Tx tx, AccountConfig cfg, DateOnly asOf, long? partyId = null, bool overdueOnly = false)
    {
        var names = tx.Rows("SELECT id, name FROM parties").ToDictionary(r => r.Long("id"), r => r.Str("name"));
        return DocsAsOf(tx, cfg, asOf, partyId)
            .Where(d => d.Outstanding > 0 && (!overdueOnly || d.DueDate < asOf))
            .Select(d =>
            {
                var days = asOf.DayNumber - d.DueDate.DayNumber;
                return (new AgingDocument(d.Id, d.Number, d.Date, d.DueDate, d.Total, Money.Round(d.Allocated), Money.Round(d.Outstanding),
                    Math.Max(days, 0), AgingBucket.For(days)), d.PartyId, names.GetValueOrDefault(d.PartyId, ""));
            })
            .ToList();
    }

    public static PartyAgingDetail AgingDetail(Tx tx, AccountConfig cfg, long partyId, DateOnly asOf)
    {
        var documents = DocsAsOf(tx, cfg, asOf, partyId).Where(d => d.Outstanding > 0).Select(d =>
        {
            var days = asOf.DayNumber - d.DueDate.DayNumber;
            return new AgingDocument(d.Id, d.Number, d.Date, d.DueDate, d.Total, Money.Round(d.Allocated), Money.Round(d.Outstanding),
                Math.Max(days, 0), AgingBucket.For(days));
        }).ToList();
        var payments = PaymentsAsOf(tx, cfg, asOf, partyId).Where(p => p.Unallocated > 0)
            .Select(p => new UnallocatedPayment(p.Id, p.Number, p.Date, p.Amount, Money.Round(p.Unallocated))).ToList();
        var totals = new AgingAmounts
        {
            Buckets = Bucketise(documents.Select(d => (d.DaysOverdue, d.Outstanding))),
            Total = Money.Round(documents.Sum(d => d.Outstanding)),
            Unallocated = Money.Round(payments.Sum(p => p.Unallocated)),
            Journal = Money.Round(JournalEffect(tx, cfg, asOf, partyId).GetValueOrDefault(partyId)),
            Opening = Money.Round(Openings(tx, cfg, partyId).GetValueOrDefault(partyId)),
        };
        return new PartyAgingDetail(asOf, totals, documents, payments);
    }

    // ------------------------------------------------------------------ ledger

    /// <summary>A ledger line before its running balance is known; Order breaks ties within a date.</summary>
    public sealed record Pending(LedgerEntry Entry, decimal Effect, string Order);

    /// <summary>
    /// Party ledger (statement) from invoices/bills, payments and their cancellations. A cancellation
    /// appears as a reversal on its own date, so history is never rewritten. Without
    /// <paramref name="start"/> the ledger starts from the party's opening balance.
    /// </summary>
    public static PartyLedger Ledger(Tx tx, AccountConfig cfg, long partyId, DateOnly? start = null, DateOnly? end = null)
    {
        var endDate = end ?? tx.Today;
        var opening = start is { } s
            ? BalanceAsOf(tx, cfg, s.AddDays(-1), partyId)
            : Openings(tx, cfg, partyId).GetValueOrDefault(partyId);
        var args = new { partyId, start, end = endDate };
        const string range = "BETWEEN COALESCE(@start, '0000-01-01') AND @end";
        var entries = new List<Pending>();

        foreach (var r in tx.Rows($"SELECT * FROM {cfg.DocTable} WHERE party_id = @partyId AND date {range}", args))
            entries.Add(DocumentEntry(cfg, r, reversal: false));
        foreach (var r in tx.Rows($"SELECT * FROM {cfg.DocTable} WHERE party_id = @partyId AND status = 'CANCELLED' AND cancelled_on {range}", args))
            entries.Add(DocumentEntry(cfg, r, reversal: true));
        const string paymentSql = "SELECT p.*, a.name AS account_name FROM {0} p JOIN money_accounts a ON a.id = p.account_id WHERE p.party_id = @partyId AND ";
        foreach (var r in tx.Rows(string.Format(paymentSql, cfg.PaymentTable) + $"p.date {range}", args))
            entries.Add(PaymentEntry(cfg, r, reversal: false));
        foreach (var r in tx.Rows(string.Format(paymentSql, cfg.PaymentTable) + $"p.status = 'CANCELLED' AND p.cancelled_on {range}", args))
            entries.Add(PaymentEntry(cfg, r, reversal: true));
        entries.AddRange(JournalService.PartyLines(tx, cfg, partyId, start, endDate));

        var balance = opening;
        decimal totalDebit = 0, totalCredit = 0;
        var result = new List<LedgerEntry>();
        foreach (var e in entries.OrderBy(e => e.Entry.Date).ThenBy(e => e.Order, StringComparer.Ordinal))
        {
            totalDebit += e.Entry.Debit;
            totalCredit += e.Entry.Credit;
            balance += e.Effect;
            result.Add(e.Entry with { Balance = Money.Round(balance) });
        }
        return new PartyLedger(Money.Round(opening), Money.Round(balance), Money.Round(totalDebit), Money.Round(totalCredit), start, endDate, result);
    }

    private static Pending DocumentEntry(AccountConfig cfg, Row r, bool reversal)
    {
        var number = r.Str("number");
        var description = $"{cfg.DocLabel} {number}" + (reversal ? $" cancelled: {r.Str("cancel_reason")}" : "");
        return Entry(cfg, r, r.Money("total_amount"), isDocument: true, reversal, number, description);
    }

    private static Pending PaymentEntry(AccountConfig cfg, Row r, bool reversal)
    {
        var reference = r.Str("reference_number");
        var description = $"{cfg.PayLabel} - {r.Str("account_name")}" + (reference.Length > 0 ? $" ({reference})" : "")
                          + (reversal ? $" cancelled: {r.Str("cancel_reason")}" : "");
        return Entry(cfg, r, r.Money("amount"), isDocument: false, reversal, r.Str("number"), description);
    }

    private static Pending Entry(AccountConfig cfg, Row r, decimal amount, bool isDocument, bool reversal, string number, string description)
    {
        // Effect on "amount owed": documents increase it, payments decrease it; a cancellation undoes it.
        var effect = isDocument ? amount : -amount;
        if (reversal)
            effect = -effect;
        var increasesDebit = effect > 0 == cfg.DocumentIsDebit;
        var label = isDocument ? cfg.DocLabel : cfg.PayLabel;
        var entry = new LedgerEntry(
            reversal ? r.Date("cancelled_on") : r.Date("date"),
            number,
            r.Long("id"),
            isDocument ? "document" : "payment",
            reversal ? $"{label} Cancellation" : label,
            description,
            increasesDebit ? Math.Abs(effect) : 0m,
            increasesDebit ? 0m : Math.Abs(effect),
            isDocument && !reversal ? r.Date("due_date") : null,
            isDocument && !reversal ? r.Str("payment_status") : null,
            r.Str("status"),
            0m);
        return new Pending(entry, effect, reversal ? r.Str("cancelled_at") : r.Str("created_at"));
    }

    // ------------------------------------------------------------------ summaries

    /// <summary>Party profile figures: totals, outstanding, overdue (as of today), advances and available credit.</summary>
    public static PartySummary Summary(Tx tx, AccountConfig cfg, Party party)
    {
        var d = tx.Row(
            $"""
            SELECT COALESCE(SUM(total_amount), 0) AS total, COALESCE(SUM(amount_paid), 0) AS paid,
                   COALESCE(SUM(CASE WHEN due_date < @today THEN total_amount - amount_paid END), 0) AS overdue,
                   COALESCE(SUM(CASE WHEN due_date >= @today THEN total_amount - amount_paid END), 0) AS current,
                   COUNT(*) AS count, MAX(date) AS last
            FROM {cfg.DocTable} WHERE party_id = @id AND status = 'ACTIVE'
            """, new { id = party.Id, today = tx.Today })!;
        var p = tx.Row(
            $"""
            SELECT COALESCE(SUM(amount), 0) AS total, COALESCE(SUM(amount - allocated_amount), 0) AS unallocated,
                   COUNT(*) AS count, MAX(date) AS last
            FROM {cfg.PaymentTable} WHERE party_id = @id AND status = 'ACTIVE'
            """, new { id = party.Id })!;
        var outstanding = Money.Round(d.Money("total") - d.Money("paid"));
        var journal = Money.Round(JournalEffect(tx, cfg, partyId: party.Id).GetValueOrDefault(party.Id));
        var opening = Money.Round(Openings(tx, cfg, party.Id).GetValueOrDefault(party.Id));
        var net = Money.Round(outstanding - p.Money("unallocated") + journal + opening);
        var last = new[] { d.NullableDate("last"), p.NullableDate("last") }.Where(x => x is not null).Max();
        return new PartySummary(party.Id, d.Money("total"), (int)d.Long("count"), p.Money("total"), (int)p.Long("count"), outstanding,
            d.Money("overdue"), d.Money("current"), p.Money("unallocated"), journal, opening, net, last, party.CreditLimit,
            party.CreditLimit > 0 ? Money.Round(party.CreditLimit - net) : null);
    }

    /// <summary>Company-wide outstanding and overdue totals (dashboard).</summary>
    public static AccountsOverview Overview(Tx tx, AccountConfig cfg)
    {
        var d = tx.Row(
            $"""
            SELECT COALESCE(SUM(total_amount - amount_paid), 0) AS outstanding,
                   COALESCE(SUM(CASE WHEN due_date < @today THEN total_amount - amount_paid END), 0) AS overdue
            FROM {cfg.DocTable} WHERE status = 'ACTIVE'
            """, new { today = tx.Today })!;
        var unallocated = Money.FromPaisa(tx.Scalar<long>($"SELECT COALESCE(SUM(amount - allocated_amount), 0) FROM {cfg.PaymentTable} WHERE status = 'ACTIVE'"));
        var journal = JournalEffect(tx, cfg).Values.Sum();
        var openings = Openings(tx, cfg).Values.ToList();
        var opening = openings.Sum();
        var owed = openings.Where(v => v > 0).Sum();
        // Opening balances count as outstanding (or, when in the party's favour, as advances).
        return new AccountsOverview(Money.Round(d.Money("outstanding") + owed), d.Money("overdue"),
            Money.Round(unallocated + owed - opening), Money.Round(journal), Money.Round(opening));
    }
}
