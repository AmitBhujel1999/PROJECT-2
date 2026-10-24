using Accounting.Core.CashBank;
using Accounting.Core.Data;
using Accounting.Core.Journal;
using Accounting.Core.Parties;
using Accounting.Core.Products;
using Accounting.Core.Security;

namespace Accounting.Core.Reports;

/// <summary>Debit / credit pair of a signed (Dr +) amount.</summary>
public readonly record struct DebitCredit(decimal Debit, decimal Credit)
{
    public static DebitCredit Of(decimal signed) => new(Money.Round(Math.Max(signed, 0)), Money.Round(Math.Max(-signed, 0)));
}

public sealed record OpeningRow(long Id, string Name, string Detail, bool IsActive, decimal OpeningBalance, string OpeningSide)
{
    public decimal Signed => OpeningSide == "DR" ? OpeningBalance : -OpeningBalance;
}

public sealed record OpeningStockRow(long Id, string Name, string SkuCode, string Unit, bool IsActive, decimal OpeningStock, decimal OpeningRate)
{
    public decimal OpeningAmount => Money.Round(OpeningStock * OpeningRate);
}

public sealed record OpeningBalanceSheet(
    IReadOnlyList<OpeningRow> Cash, IReadOnlyList<OpeningRow> Ledgers, IReadOnlyList<OpeningRow> Customers,
    IReadOnlyList<OpeningRow> Vendors, IReadOnlyList<OpeningStockRow> Stock, IReadOnlyDictionary<string, DebitCredit> Totals)
{
    public decimal TotalDebit => Money.Round(Totals.Values.Sum(t => t.Debit));
    public decimal TotalCredit => Money.Round(Totals.Values.Sum(t => t.Credit));
    /// <summary>Shown on the capital side of the balance sheet; zero once capital is entered.</summary>
    public DebitCredit Difference => DebitCredit.Of(TotalDebit - TotalCredit);
}

public sealed record OpeningChange(long Id, decimal OpeningBalance, string OpeningSide);

public sealed record StockOpeningChange(long Id, decimal OpeningStock, decimal OpeningRate);

public sealed record OpeningChanges
{
    public IReadOnlyList<OpeningChange> Cash { get; init; } = [];
    public IReadOnlyList<OpeningChange> Ledgers { get; init; } = [];
    public IReadOnlyList<OpeningChange> Customers { get; init; } = [];
    public IReadOnlyList<OpeningChange> Vendors { get; init; } = [];
    public IReadOnlyList<StockOpeningChange> Stock { get; init; } = [];
}

/// <summary>
/// Every opening amount in the books on one screen, ported from backend/apps/reports/openings.py.
/// Openings live on their own records; changes are saved through each module's own service, so
/// audit and stock rules apply.
/// </summary>
public static class OpeningBalances
{
    public static readonly IReadOnlyDictionary<string, string> SectionPermissions = new Dictionary<string, string>
    {
        ["cash"] = "cashbank.manage",
        ["ledgers"] = "journal.manage_ledgers",
        ["customers"] = "parties.manage",
        ["vendors"] = "parties.manage",
        ["stock"] = "products.manage",
    };

    public static OpeningBalanceSheet Get(Tx tx)
    {
        var cash = MoneyAccounts.List(tx).OrderBy(a => a.Name)
            .Select(a => new OpeningRow(a.Id, a.Name, AccountKind.Display(a.Kind), a.IsActive, a.OpeningBalance, "DR")).ToList();
        var ledgers = JournalService.Ledgers(tx).OrderBy(l => l.Group).ThenBy(l => l.Name)
            .Select(l => new OpeningRow(l.Id, l.Name, l.GroupLabel, l.IsActive, l.OpeningBalance, l.OpeningSide)).ToList();
        List<OpeningRow> Parties(PartyType type) =>
            tx.Rows("SELECT * FROM parties WHERE type = @type ORDER BY name COLLATE NOCASE", new { type = type.ToCode() }).Select(PartyService.Map)
                .Select(p => new OpeningRow(p.Id, p.Name, p.Phone, p.IsActive, p.OpeningBalance, p.OpeningSide)).ToList();
        var customers = Parties(PartyType.Customer);
        var vendors = Parties(PartyType.Vendor);
        var stock = tx.Rows("SELECT * FROM products ORDER BY name COLLATE NOCASE").Select(ProductService.Map)
            .Select(p => new OpeningStockRow(p.Id, p.Name, p.SkuCode, p.Unit.Display(), p.IsActive, p.OpeningStock, p.OpeningRate)).ToList();
        static DebitCredit Total(IEnumerable<OpeningRow> rows) => DebitCredit.Of(rows.Sum(r => r.Signed));
        var totals = new Dictionary<string, DebitCredit>
        {
            ["cash"] = Total(cash),
            ["ledgers"] = Total(ledgers),
            ["customers"] = Total(customers),
            ["vendors"] = Total(vendors),
            // Stock is valued from the posted opening movements (the cost they were entered at).
            ["stock"] = DebitCredit.Of(Statements.OpeningStockValue(tx, DateOnly.MaxValue)),
        };
        return new OpeningBalanceSheet(cash, ledgers, customers, vendors, stock, totals);
    }

    /// <summary>Apply changed openings. Each section needs its own permission. Returns the number of records changed.</summary>
    public static int Save(Tx tx, Actor actor, OpeningChanges changes)
    {
        tx.RequireWrite();
        foreach (var (section, any) in new[] { ("cash", changes.Cash.Count), ("ledgers", changes.Ledgers.Count), ("customers", changes.Customers.Count),
                     ("vendors", changes.Vendors.Count), ("stock", changes.Stock.Count) })
            if (any > 0)
                Permissions.Require(actor, SectionPermissions[section]);
        var changed = 0;
        foreach (var c in changes.Cash)
        {
            var account = MoneyAccounts.Get(tx, c.Id);
            var amount = Amount(c.OpeningBalance, account.Name);
            if (amount != account.OpeningBalance)
            {
                MoneyAccounts.Update(tx, actor, account.Id, account with { OpeningBalance = amount });
                changed++;
            }
        }
        foreach (var c in changes.Ledgers)
        {
            var ledger = JournalService.GetLedger(tx, c.Id);
            var (amount, side) = (Amount(c.OpeningBalance, ledger.Name), Side(c.OpeningSide, ledger.Name));
            if ((amount, side) != (ledger.OpeningBalance, ledger.OpeningSide))
            {
                JournalService.UpdateLedger(tx, actor, ledger.Id, new LedgerInput(ledger.Name, ledger.Group, amount, side, ledger.Notes));
                changed++;
            }
        }
        foreach (var (list, type) in new[] { (changes.Customers, PartyType.Customer), (changes.Vendors, PartyType.Vendor) })
            foreach (var c in list)
            {
                var party = PartyService.Get(tx, c.Id, type);
                var (amount, side) = (Amount(c.OpeningBalance, party.Name), Side(c.OpeningSide, party.Name));
                if ((amount, side) != (party.OpeningBalance, party.OpeningSide))
                {
                    PartyService.Update(tx, actor, party.Id, PartyInput.From(party) with { OpeningBalance = amount, OpeningSide = side });
                    changed++;
                }
            }
        foreach (var c in changes.Stock)
        {
            var product = ProductService.Get(tx, c.Id);
            var quantity = Money.Qty(c.OpeningStock);
            if (quantity < 0)
                throw new BusinessError($"{product.Name}: quantity cannot be negative.", "INVALID_QUANTITY");
            var rate = Amount(c.OpeningRate, product.Name);
            if (rate != product.OpeningRate)
            {
                // Posted opening stock keeps the cost it was entered at.
                if (product.OpeningStock > 0)
                    throw new BusinessError($"{product.Name}: the opening rate can only be changed while the opening quantity is 0.", "OPENING_RATE_LOCKED");
                product = ProductService.Update(tx, actor, product.Id, ProductInput.From(product) with { OpeningRate = rate });
                changed++;
            }
            if (quantity != product.OpeningStock)
            {
                ProductService.Update(tx, actor, product.Id, ProductInput.From(product) with { OpeningStock = quantity });
                changed++;
            }
        }
        return changed;
    }

    private static decimal Amount(decimal value, string label)
    {
        var amount = Money.Round(value);
        return amount >= 0 ? amount : throw new BusinessError($"{label}: amount cannot be negative.", "INVALID_AMOUNT");
    }

    private static string Side(string value, string label) =>
        value is "DR" or "CR" ? value : throw new BusinessError($"{label}: side must be Dr or Cr.", "INVALID_SIDE");
}
