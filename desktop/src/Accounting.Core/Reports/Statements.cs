using Accounting.Core.CashBank;
using Accounting.Core.Data;
using Accounting.Core.Journal;
using Accounting.Core.Parties;

namespace Accounting.Core.Reports;

/// <summary>One line of a statement. Link names the screen that explains it (e.g. "stock", "receivables").</summary>
public sealed record StatementRow(long? Id, string Name, decimal Amount, string? Link = null);

public sealed record StatementGroup(string Key, string Label, IReadOnlyList<StatementRow> Rows)
{
    public decimal Total => Money.Round(Rows.Sum(r => r.Amount));
}

public sealed record ProfitAndLoss(
    DateOnly? Start, DateOnly End, decimal Sales, IReadOnlyList<StatementRow> DirectIncome, decimal OpeningStock,
    decimal OpeningStockAdded, decimal Purchases, decimal ClosingStock, decimal CostOfGoodsSold,
    IReadOnlyList<StatementRow> DirectExpenses, decimal GrossProfit, IReadOnlyList<StatementRow> IndirectIncome,
    IReadOnlyList<StatementRow> ExpenseCategories, IReadOnlyList<StatementRow> IndirectExpenses,
    decimal TotalIndirectExpenses, decimal NetProfit);

public sealed record BalanceSheet(
    DateOnly AsOf, IReadOnlyList<StatementGroup> Liabilities, IReadOnlyList<StatementGroup> Assets,
    decimal TotalLiabilities, decimal TotalAssets, decimal NetProfit)
{
    public decimal Difference => Money.Round(TotalAssets - TotalLiabilities);
}

public sealed record CashFlow(
    DateOnly Start, DateOnly End, decimal OpeningCash, IReadOnlyList<StatementRow> Operating, IReadOnlyList<StatementRow> Investing,
    IReadOnlyList<StatementRow> Financing, decimal ClosingCash, IReadOnlyList<StatementRow> Accounts)
{
    public decimal NetOperating => Money.Round(Operating.Sum(r => r.Amount));
    public decimal NetInvesting => Money.Round(Investing.Sum(r => r.Amount));
    public decimal NetFinancing => Money.Round(Financing.Sum(r => r.Amount));
    public decimal NetChange => Money.Round(NetOperating + NetInvesting + NetFinancing);
    /// <summary>Opening + net change - closing; zero when the books agree.</summary>
    public decimal Difference => Money.Round(OpeningCash + NetChange - ClosingCash);
}

public sealed record TrialRow(string Name, decimal Debit, decimal Credit, long? Id = null, string? Link = null);

public sealed record TrialGroup(string Key, string Label, IReadOnlyList<TrialRow> Rows)
{
    private decimal Net => Money.Round(Rows.Sum(r => r.Debit - r.Credit));
    public decimal Debit => Math.Max(Net, 0);
    public decimal Credit => Math.Max(-Net, 0);
}

public sealed record TrialBalance(DateOnly? Start, DateOnly End, IReadOnlyList<TrialGroup> Groups)
{
    public decimal TotalDebit => Money.Round(Groups.Sum(g => g.Debit));
    public decimal TotalCredit => Money.Round(Groups.Sum(g => g.Credit));
    public decimal Difference => Money.Round(TotalDebit - TotalCredit);
}

/// <summary>
/// Financial statements derived from the books, ported from backend/apps/reports/statements.py.
/// Nothing is posted twice: every figure comes from the entries the program already keeps.
///
/// Rules
/// - Cancelled documents count as if they never happened, everywhere (stock too).
/// - Amounts exclude VAT. VAT collected minus VAT paid is a liability (or an asset when negative).
/// - Closing stock is valued at weighted average cost of all units received up to the date.
/// - Opening balances have no matching entry, so their total shows as "Difference in opening
///   balances" on the capital side, as Tally/Busy do.
/// </summary>
public static class Statements
{
    // Stock movements, leaving out everything that belongs to a cancelled sale or purchase.
    private const string LiveMovements =
        """
        (SELECT m.* FROM stock_movements m
         WHERE NOT (m.reference_type = 'sale' AND m.reference_id IN (SELECT id FROM sales WHERE status <> 'ACTIVE'))
           AND NOT (m.reference_type = 'purchase' AND m.reference_id IN (SELECT id FROM purchases WHERE status <> 'ACTIVE')))
        """;

    // ------------------------------------------------------------------ stock

    /// <summary>Closing stock value at weighted average cost at the end of <paramref name="asOf"/>.</summary>
    public static decimal StockValue(Tx tx, DateOnly asOf)
    {
        var rows = tx.Rows(
            $"""
             SELECT m.product_id, p.purchase_price,
                    SUM(m.quantity_in - m.quantity_out) AS qty,
                    SUM(CASE WHEN m.transaction_type IN ('OPENING_STOCK', 'PURCHASE') THEN m.quantity_in - m.quantity_out ELSE 0 END) AS received,
                    SUM(CASE WHEN m.transaction_type IN ('OPENING_STOCK', 'PURCHASE') THEN (m.quantity_in - m.quantity_out) * m.unit_cost ELSE 0 END) AS received_cost
             FROM {LiveMovements} m JOIN products p ON p.id = m.product_id
             WHERE m.date <= @asOf
             GROUP BY m.product_id
             """, new { asOf });
        decimal total = 0;
        foreach (var r in rows)
        {
            var qty = r.Qty("qty");
            if (qty <= 0)
                continue;
            var units = r.Qty("received");
            // received_cost is thousandths x paisa.
            var rate = units > 0 ? r.Long("received_cost") / 100_000m / units : r.Money("purchase_price");
            total += qty * rate;
        }
        return Money.Round(total);
    }

    /// <summary>
    /// Weighted average cost per unit of every product at the end of <paramref name="asOf"/>: the cost of all
    /// units received so far (opening stock and purchases) over their number, as the stock value uses.
    /// Products never received fall back to their purchase price.
    /// </summary>
    public static Dictionary<long, decimal> AverageCosts(Tx tx, DateOnly asOf) =>
        tx.Rows(
            $"""
             SELECT p.id, p.purchase_price,
                    COALESCE(SUM(CASE WHEN m.transaction_type IN ('OPENING_STOCK', 'PURCHASE') THEN m.quantity_in - m.quantity_out END), 0) AS received,
                    COALESCE(SUM(CASE WHEN m.transaction_type IN ('OPENING_STOCK', 'PURCHASE') THEN (m.quantity_in - m.quantity_out) * m.unit_cost END), 0) AS received_cost
             FROM products p LEFT JOIN {LiveMovements} m ON m.product_id = p.id AND m.date <= @asOf
             GROUP BY p.id
             """, new { asOf })
            .ToDictionary(r => r.Long("id"), r =>
            {
                var units = r.Qty("received");
                // received_cost is thousandths x paisa.
                return units > 0 ? r.Long("received_cost") / 100_000m / units : r.Money("purchase_price");
            });

    /// <summary>Cost of all opening-stock movements up to <paramref name="end"/> (goods brought in at the start).</summary>
    public static decimal OpeningStockValue(Tx tx, DateOnly end) =>
        Money.Round(tx.Scalar<long>(
            $"SELECT COALESCE(SUM((m.quantity_in - m.quantity_out) * m.unit_cost), 0) FROM {LiveMovements} m WHERE m.date <= @end AND m.transaction_type = 'OPENING_STOCK'",
            new { end }) / 100_000m);

    // ------------------------------------------------------------------ profit & loss

    private const string ActiveLines = "journal_lines l JOIN journal_vouchers v ON v.id = l.voucher_id AND v.status = 'ACTIVE'";

    private static decimal SumActive(Tx tx, string table, string column, DateOnly? start, DateOnly end) =>
        Money.FromPaisa(tx.Scalar<long>(
            $"SELECT COALESCE(SUM({column}), 0) FROM {table} WHERE status = 'ACTIVE' AND date <= @end AND (@start IS NULL OR date >= @start)",
            new { start, end }));

    /// <summary>Journal amounts on income or expense ledgers in the period, by ledger (with their group).</summary>
    private static List<(StatementRow Row, string Group)> LedgerMovement(Tx tx, DateOnly? start, DateOnly end, string nature)
    {
        var groups = AccountGroup.All.Where(g => g.Nature == nature).Select(g => g.Code).ToList();
        var sign = nature == "EXPENSE" ? 1 : -1; // expenses are debits, income credits
        return tx.Rows(
                $"""
                 SELECT lg.id, lg.name, lg."group", SUM(l.debit) - SUM(l.credit) AS net
                 FROM {ActiveLines} JOIN ledger_accounts lg ON lg.id = l.ledger_id
                 WHERE lg."group" IN @groups AND v.date <= @end AND (@start IS NULL OR v.date >= @start)
                 GROUP BY lg.id ORDER BY lg.name
                 """, new { groups, start, end })
            .Select(r => (new StatementRow(r.Long("id"), r.Str("name"), Money.Round(sign * r.Money("net")), "ledger"), r.Str("group")))
            .Where(x => x.Item1.Amount != 0)
            .ToList();
    }

    /// <summary>Expenses (excluding tax) plus journal amounts per expense category, largest first.</summary>
    private static List<StatementRow> ExpenseCategories(Tx tx, DateOnly? start, DateOnly end)
    {
        var totals = tx.Rows(
                "SELECT category_id, SUM(amount) AS t FROM expenses WHERE status = 'ACTIVE' AND date <= @end AND (@start IS NULL OR date >= @start) GROUP BY category_id",
                new { start, end })
            .ToDictionary(r => r.Long("category_id"), r => r.Money("t"));
        foreach (var (id, amount) in JournalService.ExpenseCategoryTotals(tx, start, end))
            totals[id] = totals.GetValueOrDefault(id) + amount;
        var names = tx.Rows("SELECT id, name FROM expense_categories").ToDictionary(r => r.Long("id"), r => r.Str("name"));
        return totals.Where(t => t.Value != 0)
            .Select(t => new StatementRow(t.Key, names.GetValueOrDefault(t.Key, ""), Money.Round(t.Value), "expense-category"))
            .OrderByDescending(r => r.Amount).ToList();
    }

    public static ProfitAndLoss ProfitAndLoss(Tx tx, DateOnly? start, DateOnly end)
    {
        var sales = SumActive(tx, "sales", "taxable_amount", start, end);
        var purchases = SumActive(tx, "purchases", "taxable_amount", start, end);
        decimal opening, broughtIn;
        if (start is { } s)
        {
            opening = StockValue(tx, s.AddDays(-1));
            // Opening stock entered (or corrected) during the period is goods received in the period.
            broughtIn = Money.Round(OpeningStockValue(tx, end) - OpeningStockValue(tx, s.AddDays(-1)));
        }
        else
        {
            opening = 0;
            broughtIn = OpeningStockValue(tx, end);
        }
        var closing = StockValue(tx, end);
        var cogs = Money.Round(opening + broughtIn + purchases - closing);

        var income = LedgerMovement(tx, start, end, "INCOME");
        var expenses = LedgerMovement(tx, start, end, "EXPENSE");
        var directIncome = income.Where(x => x.Group == "DIRECT_INCOME").Select(x => x.Row).ToList();
        var indirectIncome = income.Where(x => x.Group != "DIRECT_INCOME").Select(x => x.Row).ToList();
        var directExpenses = expenses.Where(x => x.Group == "DIRECT_EXPENSES").Select(x => x.Row).ToList();
        var indirectLedgers = expenses.Where(x => x.Group != "DIRECT_EXPENSES").Select(x => x.Row).ToList();
        var categories = ExpenseCategories(tx, start, end);

        static decimal Total(IEnumerable<StatementRow> rows) => Money.Round(rows.Sum(r => r.Amount));
        var gross = Money.Round(sales + Total(directIncome) - cogs - Total(directExpenses));
        var indirectTotal = Money.Round(Total(categories) + Total(indirectLedgers));
        var net = Money.Round(gross + Total(indirectIncome) - indirectTotal);
        return new ProfitAndLoss(start, end, sales, directIncome, opening, broughtIn, purchases, closing, cogs, directExpenses,
            gross, indirectIncome, categories, indirectLedgers, indirectTotal, net);
    }

    // ------------------------------------------------------------------ balance sheet

    /// <summary>
    /// What each party owes us (customers) or we owe them (vendors), counting active documents and
    /// payments, journal lines and opening balances.
    /// </summary>
    public static Dictionary<long, decimal> PartyOwed(Tx tx, DateOnly asOf)
    {
        var owed = new Dictionary<long, decimal>();
        void Add(long id, decimal amount) => owed[id] = owed.GetValueOrDefault(id) + amount;
        foreach (var (table, column, sign) in new[] { ("sales", "total_amount", 1), ("customer_receipts", "amount", -1), ("purchases", "total_amount", 1), ("vendor_payments", "amount", -1) })
            foreach (var r in tx.Rows($"SELECT party_id, SUM({column}) AS t FROM {table} WHERE status = 'ACTIVE' AND date <= @asOf GROUP BY party_id", new { asOf }))
                Add(r.Long("party_id"), sign * r.Money("t"));
        foreach (var r in tx.Rows(
                     $"SELECT l.party_id, p.type, SUM(l.debit) - SUM(l.credit) AS net FROM {ActiveLines} JOIN parties p ON p.id = l.party_id WHERE v.date <= @asOf GROUP BY l.party_id",
                     new { asOf }))
            Add(r.Long("party_id"), r.Str("type") == "CUSTOMER" ? r.Money("net") : -r.Money("net"));
        foreach (var p in tx.Rows("SELECT * FROM parties WHERE opening_balance > 0").Select(PartyService.Map))
            Add(p.Id, p.Type == PartyType.Customer ? p.SignedOpening : -p.SignedOpening);
        return owed.Where(o => o.Value != 0).ToDictionary(o => o.Key, o => Money.Round(o.Value));
    }

    private sealed record PartyTotals(decimal Debtors, decimal CustomerAdvances, decimal Creditors, decimal SupplierAdvances);

    private static PartyTotals PartyBalances(Tx tx, DateOnly asOf)
    {
        var owed = PartyOwed(tx, asOf);
        var types = tx.Rows("SELECT id, type FROM parties").ToDictionary(r => r.Long("id"), r => r.Str("type"));
        decimal debtors = 0, customerAdvances = 0, creditors = 0, supplierAdvances = 0;
        foreach (var (id, amount) in owed)
        {
            if (types.GetValueOrDefault(id) == "CUSTOMER")
            {
                if (amount > 0) debtors += amount;
                else customerAdvances -= amount;
            }
            else if (amount > 0)
                creditors += amount;
            else
                supplierAdvances -= amount;
        }
        return new PartyTotals(Money.Round(debtors), Money.Round(customerAdvances), Money.Round(creditors), Money.Round(supplierAdvances));
    }

    /// <summary>VAT collected on sales minus VAT paid on purchases and expenses (positive = payable).</summary>
    public static decimal VatBalance(Tx tx, DateOnly asOf) =>
        Money.Round(SumActive(tx, "sales", "tax_amount", null, asOf) - SumActive(tx, "purchases", "tax_amount", null, asOf)
                    - SumActive(tx, "expenses", "tax_amount", null, asOf));

    /// <summary>Signed (Dr +) total of every opening balance; it has no matching entry.</summary>
    public static decimal OpeningDifference(Tx tx) =>
        Money.Round(MoneyAccounts.List(tx).Sum(a => a.OpeningBalance)
                    + JournalService.Ledgers(tx).Sum(l => l.SignedOpening)
                    + tx.Rows("SELECT * FROM parties WHERE opening_balance > 0").Select(PartyService.Map).Sum(p => p.SignedOpening));

    public static BalanceSheet BalanceSheet(Tx tx, DateOnly asOf)
    {
        var cash = MoneyAccounts.List(tx)
            .Select(a => new StatementRow(a.Id, a.Name, CashBook.Balance(tx, a.Id, asOf), "cash-bank"))
            .Where(r => r.Amount != 0).OrderBy(r => r.Name).ToList();
        var parties = PartyBalances(tx, asOf);
        var vat = VatBalance(tx, asOf);
        var stock = StockValue(tx, asOf);
        var balances = JournalService.LedgerBalances(tx, asOf);
        var byGroup = new Dictionary<string, List<StatementRow>>();
        foreach (var l in JournalService.Ledgers(tx).OrderBy(l => l.Name))
        {
            if (l.Nature is not ("ASSET" or "LIABILITY") || balances.GetValueOrDefault(l.Id).Signed is var signed && signed == 0)
                continue;
            // Assets show debit-positive, liabilities credit-positive.
            (byGroup.TryGetValue(l.Group, out var list) ? list : byGroup[l.Group] = []).Add(
                new StatementRow(l.Id, l.Name, Money.Round(l.Nature == "ASSET" ? signed : -signed), "ledger"));
        }
        List<StatementRow> Ledgers(string group) => byGroup.GetValueOrDefault(group) ?? [];
        static IEnumerable<StatementRow> If(bool condition, StatementRow row) => condition ? [row] : [];

        var profit = ProfitAndLoss(tx, null, asOf).NetProfit;
        var openingDiff = Money.Round(OpeningDifference(tx) + OpeningStockValue(tx, asOf));
        StatementGroup G(string key, string label, IEnumerable<StatementRow> rows) => new(key, label, rows.ToList());

        var liabilities = new[]
        {
            G("CAPITAL", "Capital Account", [.. Ledgers("CAPITAL"), new StatementRow(null, "Profit & Loss A/c", profit, "profit-loss"),
                new StatementRow(null, "Difference in opening balances", openingDiff)]),
            G("RESERVES", "Reserves & Surplus", Ledgers("RESERVES")),
            G("LOANS", "Loans (Liability)", Ledgers("LOANS")),
            G("CURRENT_LIABILITIES", "Current Liabilities", [
                .. If(parties.Creditors != 0, new StatementRow(null, "Sundry Creditors", parties.Creditors, "payables")),
                .. If(parties.CustomerAdvances != 0, new StatementRow(null, "Advances from customers", parties.CustomerAdvances, "receivables")),
                .. Ledgers("CURRENT_LIABILITIES")]),
            G("DUTIES_TAXES", "Duties & Taxes", [.. If(vat > 0, new StatementRow(null, "VAT payable", vat)), .. Ledgers("DUTIES_TAXES")]),
        }.Where(g => g.Rows.Count > 0).ToList();
        var assets = new[]
        {
            G("FIXED_ASSETS", "Fixed Assets", Ledgers("FIXED_ASSETS")),
            G("INVESTMENTS", "Investments", Ledgers("INVESTMENTS")),
            G("CURRENT_ASSETS", "Current Assets", [
                .. If(stock != 0, new StatementRow(null, "Closing stock", stock, "stock")),
                .. If(parties.Debtors != 0, new StatementRow(null, "Sundry Debtors", parties.Debtors, "receivables")),
                .. If(parties.SupplierAdvances != 0, new StatementRow(null, "Advances to suppliers", parties.SupplierAdvances, "payables")),
                .. If(vat < 0, new StatementRow(null, "VAT receivable", -vat)),
                .. Ledgers("CURRENT_ASSETS")]),
            G("CASH_BANK", "Cash & Bank", cash),
            G("LOANS_ADVANCES", "Loans & Advances (Asset)", Ledgers("LOANS_ADVANCES")),
        }.Where(g => g.Rows.Count > 0).ToList();
        return new BalanceSheet(asOf, liabilities, assets, Money.Round(liabilities.Sum(g => g.Total)), Money.Round(assets.Sum(g => g.Total)), profit);
    }

    // ------------------------------------------------------------------ cash flow

    private static readonly HashSet<string> Investing = ["FIXED_ASSETS", "INVESTMENTS"];
    private static readonly HashSet<string> Financing = ["CAPITAL", "RESERVES", "LOANS"];

    /// <summary>Cash flow (direct method) from the cash/bank books.</summary>
    public static CashFlow CashFlow(Tx tx, DateOnly start, DateOnly end)
    {
        if (end < start)
            throw new ValidationError("end_date", "End date cannot be before the start date.");
        var accounts = MoneyAccounts.List(tx);
        var opening = Money.Round(accounts.Sum(a => CashBook.Balance(tx, a.Id, start.AddDays(-1))));
        var closing = Money.Round(accounts.Sum(a => CashBook.Balance(tx, a.Id, end)));
        var receipts = SumActive(tx, "customer_receipts", "amount", start, end);
        var payments = SumActive(tx, "vendor_payments", "amount", start, end);
        var expenses = SumActive(tx, "expenses", "total_amount", start, end);

        // Journal lines on cash/bank, classified by the ledgers the rest of the voucher touched.
        var sections = new Dictionary<string, Dictionary<string, decimal>> { ["operating"] = [], ["investing"] = [], ["financing"] = [] };
        var cashLines = tx.Rows($"SELECT l.voucher_id, l.debit, l.credit FROM {ActiveLines} WHERE l.money_account_id IS NOT NULL AND v.date BETWEEN @start AND @end",
            new { start, end }).ToList();
        var voucherIds = cashLines.Select(l => l.Long("voucher_id")).Distinct().ToList();
        var others = tx.Rows(
                """
                SELECT l.voucher_id, lg."group", lg.name FROM journal_lines l JOIN ledger_accounts lg ON lg.id = l.ledger_id
                WHERE l.voucher_id IN @voucherIds
                """, new { voucherIds })
            .GroupBy(r => r.Long("voucher_id"))
            .ToDictionary(g => g.Key, g => (Groups: g.Select(r => r.Str("group")).ToHashSet(), Names: g.Select(r => r.Str("name")).Distinct().Order().ToList()));
        foreach (var line in cashLines)
        {
            var (groups, names) = others.GetValueOrDefault(line.Long("voucher_id"), ([], []));
            var section = groups.Overlaps(Investing) ? "investing" : groups.Overlaps(Financing) ? "financing" : "operating";
            var label = names.Count > 0 ? string.Join(", ", names) : "Journal adjustments";
            sections[section][label] = sections[section].GetValueOrDefault(label) + line.Money("debit") - line.Money("credit");
        }
        List<StatementRow> Rows(string section) =>
            sections[section].Where(kv => kv.Value != 0).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new StatementRow(null, kv.Key, Money.Round(kv.Value))).ToList();
        var operating = new List<StatementRow>
        {
            new(null, "Received from customers", receipts, "receipts"),
            new(null, "Paid to suppliers", -payments, "payments"),
            new(null, "Expenses paid", -expenses, "expenses"),
        };
        operating.AddRange(Rows("operating"));
        return new CashFlow(start, end, opening, operating.Where(r => r.Amount != 0).ToList(), Rows("investing"), Rows("financing"), closing,
            accounts.Select(a => new StatementRow(a.Id, a.Name, CashBook.Balance(tx, a.Id, end), "cash-bank")).ToList());
    }

    // ------------------------------------------------------------------ trial balance

    /// <summary>
    /// Closing balance of every account on <paramref name="end"/>, grouped, in debit/credit columns.
    /// Balance-sheet accounts show their closing balance; income and expense accounts show the movement
    /// from <paramref name="start"/> (all time when null); profit made before the start shows as
    /// "Profit &amp; Loss A/c" in Capital. Closing stock is left out, as in Tally/Busy.
    /// </summary>
    public static TrialBalance TrialBalance(Tx tx, DateOnly? start, DateOnly end)
    {
        static TrialRow Row(string name, decimal signed, long? id = null, string? link = null)
        {
            signed = Money.Round(signed);
            return new TrialRow(name, signed > 0 ? signed : 0, signed < 0 ? -signed : 0, id, link);
        }
        var groups = new List<(string Key, string Label, List<TrialRow> Rows)>();
        void Add(string key, string label, TrialRow row)
        {
            if (row.Debit == 0 && row.Credit == 0)
                return;
            var index = groups.FindIndex(g => g.Key == key);
            if (index < 0)
                groups.Add((key, label, [row]));
            else
                groups[index].Rows.Add(row);
        }
        string Label(string code) => AccountGroup.Get(code).Label;

        var balances = JournalService.LedgerBalances(tx, end);
        var byGroup = JournalService.Ledgers(tx).Where(l => l.Nature is "ASSET" or "LIABILITY").OrderBy(l => l.Name)
            .GroupBy(l => l.Group).ToDictionary(g => g.Key, g => g.Select(l => Row(l.Name, balances.GetValueOrDefault(l.Id).Signed, l.Id, "ledger")).ToList());
        List<TrialRow> Ledgers(string group) => byGroup.GetValueOrDefault(group) ?? [];
        var pl = ProfitAndLoss(tx, start, end);

        // Capital & liabilities
        foreach (var r in Ledgers("CAPITAL"))
            Add("CAPITAL", Label("CAPITAL"), r);
        Add("CAPITAL", Label("CAPITAL"), Row("Difference in opening balances", -(OpeningDifference(tx) + OpeningStockValue(tx, end))));
        if (start is { } s)
            Add("CAPITAL", Label("CAPITAL"), Row("Profit & Loss A/c (before this period)", -ProfitAndLoss(tx, null, s.AddDays(-1)).NetProfit, link: "profit-loss"));
        foreach (var key in new[] { "RESERVES", "LOANS", "CURRENT_LIABILITIES" })
            foreach (var r in Ledgers(key))
                Add(key, Label(key), r);
        var owed = PartyOwed(tx, end);
        var parties = tx.Rows("SELECT * FROM parties ORDER BY name COLLATE NOCASE").Select(PartyService.Map).Where(p => owed.ContainsKey(p.Id)).ToList();
        foreach (var p in parties.Where(p => p.Type == PartyType.Vendor))
            Add("SUNDRY_CREDITORS", "Sundry Creditors", Row(p.Name, -owed[p.Id], p.Id, "vendor"));
        Add("DUTIES_TAXES", Label("DUTIES_TAXES"), Row(TaxLabel(), -VatBalance(tx, end)));
        foreach (var r in Ledgers("DUTIES_TAXES"))
            Add("DUTIES_TAXES", Label("DUTIES_TAXES"), r);

        // Assets
        foreach (var key in new[] { "FIXED_ASSETS", "INVESTMENTS", "CURRENT_ASSETS" })
            foreach (var r in Ledgers(key))
                Add(key, Label(key), r);
        foreach (var p in parties.Where(p => p.Type == PartyType.Customer))
            Add("SUNDRY_DEBTORS", "Sundry Debtors", Row(p.Name, owed[p.Id], p.Id, "customer"));
        foreach (var a in MoneyAccounts.List(tx))
            Add("CASH_BANK", "Cash & Bank", Row(a.Name, CashBook.Balance(tx, a.Id, end), a.Id, "cash"));
        foreach (var r in Ledgers("LOANS_ADVANCES"))
            Add("LOANS_ADVANCES", Label("LOANS_ADVANCES"), r);

        // Trading and profit & loss accounts for the period
        Add("STOCK", "Stock-in-hand", Row("Opening stock", pl.OpeningStock + pl.OpeningStockAdded, link: "stock"));
        Add("SALES", "Sales Accounts", Row("Sales (excluding VAT)", -pl.Sales, link: "sales"));
        Add("PURCHASES", "Purchase Accounts", Row("Purchases (excluding VAT)", pl.Purchases, link: "purchases"));
        foreach (var (key, rows, sign) in new[] { ("DIRECT_INCOME", pl.DirectIncome, -1), ("DIRECT_EXPENSES", pl.DirectExpenses, 1), ("INDIRECT_INCOME", pl.IndirectIncome, -1) })
            foreach (var r in rows)
                Add(key, Label(key), Row(r.Name, sign * r.Amount, r.Id, "ledger"));
        foreach (var r in pl.ExpenseCategories)
            Add("INDIRECT_EXPENSES", Label("INDIRECT_EXPENSES"), Row(r.Name, r.Amount, r.Id, "expense-category"));
        foreach (var r in pl.IndirectExpenses)
            Add("INDIRECT_EXPENSES", Label("INDIRECT_EXPENSES"), Row(r.Name, r.Amount, r.Id, "ledger"));

        return new TrialBalance(start, end, groups.Select(g => new TrialGroup(g.Key, g.Label, g.Rows)).ToList());

        string TaxLabel() => Common.BusinessSettings.Get(tx).TaxLabel is { Length: > 0 } label ? label : "VAT";
    }
}
