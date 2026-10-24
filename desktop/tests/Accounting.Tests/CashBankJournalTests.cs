using Accounting.Core;
using Accounting.Core.Audit;
using Accounting.Core.CashBank;
using Accounting.Core.Common;
using Accounting.Core.Expenses;
using Accounting.Core.Journal;
using Accounting.Core.Payments;
using Accounting.Core.Security;
using Accounting.Core.Trade;
using Microsoft.Data.Sqlite;
using static Accounting.Tests.TestDb;

namespace Accounting.Tests;

// Ported from backend/tests/test_cashbank.py and test_journal.py.
public class CashBankJournalTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private MoneyAccount Account(string name, string kind, decimal opening) =>
        _t.Write(tx => MoneyAccounts.Create(tx, _t.Admin, new MoneyAccount { Name = name, Kind = kind, OpeningBalance = opening }));

    private MoneyAccount Bank() => Account("Nabil Bank", AccountKind.Bank, 50000);
    private MoneyAccount Cash() => Account("Cash in Hand", AccountKind.Cash, 1000);

    private PartyPayment Pay(AccountConfig cfg, long partyId, DateOnly date, decimal amount, long accountId, string reference = "") =>
        _t.Write(tx => PartyAccounts.CreatePayment(tx, _t.Admin, cfg, partyId, date, amount, accountId: accountId, referenceNumber: reference));

    private AccountBook Book(MoneyAccount a, DateOnly? start = null, DateOnly? end = null) => _t.Read(tx => CashBook.Book(tx, a.Id, start, end));

    private long CategoryId(string name) => _t.Read(tx => ExpenseService.Categories(tx)).Single(c => c.Name == name).Id;

    // ------------------------------------------------------------------ cash & bank

    [Fact]
    public void AccountCrudAndUniqueName()
    {
        var wallet = Account("eSewa", AccountKind.Wallet, 0);
        Assert.Equal(("Digital wallet", 0.00m), (AccountKind.Display(wallet.Kind), _t.Read(tx => CashBook.Balance(tx, wallet.Id))));
        Assert.Equal("DUPLICATE_ACCOUNT", Assert.Throws<BusinessError>(() => Account("esewa", AccountKind.Wallet, 0)).Code);
        _t.Write(tx => MoneyAccounts.Update(tx, _t.Admin, wallet.Id, wallet with { OpeningBalance = 2500 }));
        Assert.Equal(2500.00m, _t.Read(tx => CashBook.Balance(tx, wallet.Id)));
        _t.Write(tx => { MoneyAccounts.Delete(tx, _t.Admin, wallet.Id); return 0; });
    }

    [Fact]
    public void ReceiptPaymentExpenseAndContraInBook()
    {
        var bank = Bank();
        var cash = Cash();
        var r = Pay(AccountConfig.Receivable, _t.Customer().Id, D(2026, 9, 2), 12000, bank.Id, "TRF-1");
        Assert.Equal(("Nabil Bank", PaymentMethod.Bank), (r.AccountName, r.PaymentMethod));
        Pay(AccountConfig.Payable, _t.Vendor().Id, D(2026, 9, 5), 20000, bank.Id);
        var rentId = CategoryId("Rent");
        var e = _t.Write(tx => ExpenseService.Create(tx, _t.Admin, new ExpenseInput
            { Date = D(2026, 9, 12), CategoryId = rentId, Description = "Office rent", Amount = 15000, AccountId = bank.Id }));
        Assert.Equal("Nabil Bank", e.AccountName);
        var contra = _t.Write(tx => CashBook.CreateContra(tx, _t.Admin, D(2026, 9, 9), cash.Id, bank.Id, 500, notes: "Cash deposit"));

        var data = Book(bank);
        Assert.Equal(50000.00m, data.OpeningBalance);
        Assert.Equal(
            [("Receipt", 12000.00m, 0.00m, 62000.00m), ("Payment", 0.00m, 20000.00m, 42000.00m), ("Contra", 500.00m, 0.00m, 42500.00m), ("Expense", 0.00m, 15000.00m, 27500.00m)],
            data.Entries.Select(x => (x.VoucherType, x.MoneyIn, x.MoneyOut, x.Balance)));
        Assert.Equal("ABC Traders", data.Entries[0].Particulars);
        Assert.Equal("Transfer from Cash in Hand", data.Entries[2].Particulars);
        Assert.Equal("Rent: Office rent", data.Entries[3].Particulars);
        Assert.Equal(27500.00m, data.ClosingBalance);
        Assert.Equal(500.00m, Book(cash).ClosingBalance);
        // A period starts from the balance carried forward.
        var period = Book(bank, D(2026, 9, 6), D(2026, 9, 30));
        Assert.Equal((42000.00m, 2), (period.OpeningBalance, period.Entries.Count));
        // Cancelling removes the entry from the book.
        _t.Write(tx => CashBook.CancelContra(tx, _t.Admin, contra.Id, "wrong"));
        Assert.Equal(1000.00m, Book(cash).ClosingBalance);
        var balances = _t.Read(CashBook.Balances);
        Assert.Equal((1000.00m, 27000.00m), (balances[cash.Id], balances[bank.Id]));
    }

    [Fact]
    public void ContraRules()
    {
        var bank = Bank();
        var cash = Cash();
        Assert.Equal("SAME_ACCOUNT", Assert.Throws<BusinessError>(() => _t.Write(tx => CashBook.CreateContra(tx, _t.Admin, TestDb.Today, bank.Id, bank.Id, 10))).Code);
        _t.Write(tx => MoneyAccounts.SetActive(tx, _t.Admin, cash.Id, false));
        Assert.Equal("ACCOUNT_INACTIVE", Assert.Throws<BusinessError>(() => _t.Write(tx => CashBook.CreateContra(tx, _t.Admin, TestDb.Today, cash.Id, bank.Id, 10))).Code);
        var staff = _t.MakeUser(Role.Staff);
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => CashBook.CreateContra(tx, staff, TestDb.Today, bank.Id, cash.Id, 10)));
    }

    [Fact]
    public void UsedAccountCannotBeDeleted()
    {
        var bank = Bank();
        Pay(AccountConfig.Receivable, _t.Customer().Id, D(2026, 9, 2), 10, bank.Id);
        Assert.Equal("ACCOUNT_IN_USE", Assert.Throws<BusinessError>(() => _t.Write(tx => { MoneyAccounts.Delete(tx, _t.Admin, bank.Id); return 0; })).Code);
    }

    [Fact]
    public void SalePaidIntoChosenAccount()
    {
        var bank = Bank();
        var p = _t.Product(price: 1000, tax: 0);
        _t.MakeSale(_t.Customer(), [Item(p, 2)], payment: new PaymentInput { PayInFull = true, AccountId = bank.Id });
        Assert.Equal([("Receipt", 2000.00m)], Book(bank).Entries.Select(e => (e.VoucherType, e.MoneyIn)));
    }

    [Fact]
    public void DayBook()
    {
        var bank = Bank();
        var p = _t.Product(price: 1000, tax: 0);
        _t.MakeSale(_t.Customer(), [Item(p, 1)], D(2026, 9, 27));
        Pay(AccountConfig.Receivable, _t.Customer().Id, D(2026, 9, 27), 400, bank.Id);
        Pay(AccountConfig.Receivable, _t.Customer().Id, D(2026, 9, 26), 1, bank.Id);
        var data = _t.Read(tx => CashBook.DayBook(tx, D(2026, 9, 27), D(2026, 9, 27)));
        Assert.Equal([("Sales", 1000.00m, ""), ("Receipt", 400.00m, "Nabil Bank")], data.Entries.Select(e => (e.VoucherType, e.Amount, e.AccountName)));
        Assert.Equal([("Sales", 1000.00m), ("Receipt", 400.00m)], data.Totals);
    }

    [Fact]
    public void DefaultCashAccountCreatedForFirstExpense()
    {
        var rentId = CategoryId("Rent");
        var e = _t.Write(tx => ExpenseService.Create(tx, _t.Admin, new ExpenseInput { Date = D(2026, 9, 1), CategoryId = rentId, Description = "Tea", Amount = 100 }));
        Assert.Equal("Cash in Hand", e.AccountName);
    }

    // ------------------------------------------------------------------ journal

    private LedgerAccount Capital() => _t.Write(tx => JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Owner Capital X", "CAPITAL", OpeningSide: "CR")));
    private LedgerAccount Furniture() => _t.Write(tx => JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Furniture X", "FIXED_ASSETS", 1000, "DR")));

    private static JournalLineInput L(string account, decimal Debit = 0, decimal Credit = 0, string Narration = "") => new(account, Debit, Credit, Narration);

    private JournalVoucher Jv(DateOnly date, string narration, params JournalLineInput[] lines) =>
        _t.Write(tx => JournalService.CreateVoucher(tx, _t.Admin, date, narration, lines));

    [Fact]
    public void LedgerDefaultsSideAndUniqueName()
    {
        var vehicle = _t.Write(tx => JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Vehicle", "FIXED_ASSETS", 500)));
        Assert.Equal(("DR", "ASSET"), (vehicle.OpeningSide, vehicle.Nature));
        Assert.Equal(new DrCr(500), _t.Read(tx => JournalService.LedgerBalances(tx))[vehicle.Id]);
        Assert.Equal("CR", _t.Write(tx => JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("Partner Loan", "LOANS"))).OpeningSide);
        Assert.Equal("DUPLICATE_LEDGER", Assert.Throws<BusinessError>(() => _t.Write(tx => JournalService.CreateLedger(tx, _t.Admin, new LedgerInput("vehicle", "FIXED_ASSETS")))).Code);
        Assert.Contains(AccountGroup.All, g => g is { Code: "CAPITAL", Label: "Capital Account", Nature: "LIABILITY" });
    }

    [Fact]
    public void VoucherMustBalanceAndHaveOneSidePerLine()
    {
        var capital = Capital();
        var furniture = Furniture();
        string Code(params JournalLineInput[] lines) => Assert.Throws<BusinessError>(() => Jv(TestDb.Today, "t", lines)).Code;
        Assert.Equal("NOT_BALANCED", Code(L($"ledger:{furniture.Id}", Debit: 100), L($"ledger:{capital.Id}", Credit: 90)));
        Assert.Equal("INVALID_LINE", Code(L($"ledger:{furniture.Id}", 100, 100), L($"ledger:{capital.Id}")));
        Assert.Equal("TOO_FEW_LINES", Code(L($"ledger:{furniture.Id}", Debit: 100)));
        Assert.Equal("ACCOUNT_NOT_FOUND", Code(L("ledger:999999", Debit: 1), L($"ledger:{capital.Id}", Credit: 1)));
        Assert.Equal("ACCOUNT_REQUIRED", Code(L("bogus", Debit: 1), L($"ledger:{capital.Id}", Credit: 1)));
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("INSERT INTO journal_lines (voucher_id, line_no, debit) VALUES (1, 1, 1)")));
    }

    [Fact]
    public void LedgerBookAndCancel()
    {
        var capital = Capital();
        var furniture = Furniture();
        var v = Jv(D(2026, 9, 10), "Owner brought furniture", L($"ledger:{furniture.Id}", Debit: 25000), L($"ledger:{capital.Id}", Credit: 25000));
        Assert.Equal(("JV-000001", 25000.00m), (v.Number, v.TotalAmount));
        Assert.Equal(["Furniture X", "Owner Capital X"], v.Lines.Select(l => l.Account.Name));
        Assert.Equal(["Furniture X"], v.DebitAccounts);
        Assert.Equal(["Owner Capital X"], v.CreditAccounts);
        var book = _t.Read(tx => JournalService.Book(tx, furniture.Id));
        Assert.Equal((new DrCr(1000), new DrCr(26000)), (book.Opening, book.Closing));
        Assert.Equal("Owner brought furniture", book.Entries[0].Narration);
        Assert.Equal(("25,000.00 Cr"), _t.Read(tx => JournalService.LedgerBalances(tx))[capital.Id].ToString());
        _t.Write(tx => JournalService.CancelVoucher(tx, _t.Admin, v.Id, "wrong"));
        Assert.Equal(new DrCr(0), _t.Read(tx => JournalService.LedgerBalances(tx))[capital.Id]);
        Assert.Equal("LEDGER_IN_USE", Assert.Throws<BusinessError>(() => _t.Write(tx => { JournalService.DeleteLedger(tx, _t.Admin, capital.Id); return 0; })).Code);
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("DELETE FROM journal_vouchers")));
    }

    [Fact]
    public void CashBankLinesShowInBookAndDayBook()
    {
        var capital = Capital();
        var bank = Bank();
        Jv(D(2026, 9, 27), "", L($"cash:{bank.Id}", Debit: 10000), L($"ledger:{capital.Id}", Credit: 10000, Narration: "Capital introduced"));
        var book = Book(bank);
        Assert.Equal([("Journal", 10000.00m)], book.Entries.Select(e => (e.VoucherType, e.MoneyIn)));
        Assert.Equal(60000.00m, book.ClosingBalance);
        Assert.Equal(60000.00m, _t.Read(CashBook.Balances)[bank.Id]);
        var day = _t.Read(tx => CashBook.DayBook(tx, D(2026, 9, 27), D(2026, 9, 27)));
        Assert.Equal([("Journal", 10000.00m, "journal")], day.Entries.Select(e => (e.VoucherType, e.Amount, e.Link)));
        Assert.Equal("ACCOUNT_IN_USE", Assert.Throws<BusinessError>(() => _t.Write(tx => { MoneyAccounts.Delete(tx, _t.Admin, bank.Id); return 0; })).Code);
    }

    [Fact]
    public void PartyLinesChangeBalanceLedgerAndAging()
    {
        var capital = Capital();
        var p = _t.Product(price: 1000, tax: 0);
        _t.MakeSale(_t.Customer(), [Item(p, 2)], D(2026, 9, 1));
        // Discount allowed: credit the customer 300 (they owe less), debit an expense category.
        var misc = CategoryId("Miscellaneous");
        Jv(D(2026, 9, 5), "", L($"expense:{misc}", Debit: 300), L($"party:{_t.Customer().Id}", Credit: 300));
        // Vendor: we owe them 500 more.
        Jv(D(2026, 9, 5), "", L($"ledger:{capital.Id}", Debit: 500), L($"party:{_t.Vendor().Id}", Credit: 500));
        var ledger = _t.Read(tx => PartyLedgers.Ledger(tx, AccountConfig.Receivable, _t.Customer().Id));
        Assert.Equal(("Journal", 0.00m, 300.00m), (ledger.Entries[^1].TransactionType, ledger.Entries[^1].Debit, ledger.Entries[^1].Credit));
        Assert.Equal(1700.00m, ledger.ClosingBalance);
        var summary = _t.Read(tx => PartyLedgers.Summary(tx, AccountConfig.Receivable, _t.Customer()));
        Assert.Equal((-300.00m, 1700.00m), (summary.Journal, summary.NetBalance));
        var row = _t.Read(tx => PartyLedgers.Aging(tx, AccountConfig.Receivable, D(2026, 9, 30))).Rows.Single(r => r.PartyId == _t.Customer().Id).Amounts;
        Assert.Equal((-300.00m, 1700.00m), (row.Journal, row.NetBalance));
        var vendor = _t.Read(tx => PartyLedgers.Summary(tx, AccountConfig.Payable, _t.Vendor()));
        Assert.Equal((500.00m, 500.00m), (vendor.Journal, vendor.NetBalance));
        Assert.Equal(1700.00m, _t.Read(tx => PartyLedgers.BalanceAsOf(tx, AccountConfig.Receivable, D(2026, 9, 30), _t.Customer().Id)));
        Assert.Equal(2000.00m, _t.Read(tx => PartyLedgers.BalanceAsOf(tx, AccountConfig.Receivable, D(2026, 9, 4), _t.Customer().Id)));
        Assert.Equal(300.00m, _t.Read(tx => JournalService.ExpenseCategoryTotals(tx, D(2026, 9, 1), D(2026, 9, 30)))[misc]);
    }

    [Fact]
    public void AccountSearch()
    {
        var capital = Capital();
        var bank = Bank();
        var customer = _t.Customer();
        var keys = _t.Read(tx => JournalService.AccountOptions(tx)).Select(o => o.Key).ToHashSet();
        Assert.Superset(new HashSet<string> { $"ledger:{capital.Id}", $"cash:{bank.Id}", $"party:{customer.Id}" }, keys);
        Assert.Equal(["Nabil Bank"], _t.Read(tx => JournalService.AccountOptions(tx, "nabil")).Select(o => o.Name));
    }

    [Fact]
    public void JournalPermissions()
    {
        var accountant = _t.MakeUser(Role.Accountant);
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => JournalService.CreateLedger(tx, accountant, new LedgerInput("X", "CAPITAL"))));
        var staff = _t.MakeUser(Role.Staff);
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => JournalService.CreateVoucher(tx, staff, TestDb.Today, "", [])));
    }

    // ------------------------------------------------------------------ audit viewer

    [Fact]
    public void AuditLogViewerFilters()
    {
        var p = _t.Product("Widget");
        _t.Write(tx => Accounting.Core.Products.ProductService.SetActive(tx, _t.Admin, p.Id, false));
        var page = _t.Read(tx => AuditLog.List(tx, new AuditFilter { ModelName = "products.Product" }));
        Assert.Equal([AuditAction.Deactivate, AuditAction.Create], page.Results.Select(e => e.Action));
        Assert.Equal(1, _t.Read(tx => AuditLog.List(tx, new AuditFilter { Action = AuditAction.Deactivate })).Count);
        Assert.Equal(2, _t.Read(tx => AuditLog.List(tx, new AuditFilter { Search = "widget" })).Count);
        Assert.Equal(0, _t.Read(tx => AuditLog.List(tx, new AuditFilter { End = D(2020, 1, 1) })).Count);
    }
}
