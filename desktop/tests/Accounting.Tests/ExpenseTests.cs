using Accounting.Core;
using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Expenses;
using Accounting.Core.Security;
using Microsoft.Data.Sqlite;

namespace Accounting.Tests;

// Ported from backend/tests/test_expenses.py (the report/dashboard/PDF parts come with phase 7).
public class ExpenseTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    private ExpenseCategory Category(string name) => _t.Read(tx => ExpenseService.Categories(tx)).Single(c => c.Name == name);

    private ExpenseInput Payload(ExpenseCategory category) =>
        new() { Date = TestDb.Today, CategoryId = category.Id, Description = "September rent", Amount = 10000 };

    private Expense Create(ExpenseInput input, Actor? actor = null) => _t.Write(tx => ExpenseService.Create(tx, actor ?? _t.Admin, input));

    [Fact]
    public void CalculateExpenseRoundsTaxHalfUp()
    {
        Assert.Equal(new ExpenseTotals(1000.00m, 13.00m, 130.00m, 1130.00m), ExpenseService.Calculate(1000, 13));
        Assert.Equal(43.33m, ExpenseService.Calculate(333.33m, 13).TaxAmount); // 43.3329
        Assert.Equal(0.01m, ExpenseService.Calculate(0.05m, 13).TaxAmount);    // 0.0065
        Assert.Equal(500.00m, ExpenseService.Calculate(500, null).TotalAmount);
        Assert.Equal(new ExpenseTotals(250, 13, 32.50m, 282.50m), ExpenseService.Calculate(250, 13));
    }

    [Fact]
    public void DefaultCategoriesSeeded() => Assert.Equal(11, _t.Read(tx => ExpenseService.Categories(tx)).Count);

    [Fact]
    public void CreateExpenseCalculatesAndNumbersSequentially()
    {
        var e = Create(Payload(Category("Rent")) with { TaxRate = 13 });
        Assert.Equal(("EXP-000001", 10000.00m, 1300.00m, 11300.00m), (e.Number, e.Amount, e.TaxAmount, e.TotalAmount));
        Assert.Equal(("Rent", DocumentStatus.Active, "Cash in Hand"), (e.CategoryName, e.Status, e.AccountName));
        Assert.Equal("EXP-000002", Create(Payload(Category("Rent"))).Number);
        Assert.Equal(2, _t.Read(tx => AuditLog.Recent(tx)).Count(a => a.ModelName == "expenses.Expense" && a.Action == AuditAction.Create));
    }

    [Fact]
    public void VendorWinsOverPayee()
    {
        var e = Create(Payload(Category("Rent")) with { VendorId = _t.Vendor().Id, Payee = "Someone else" });
        Assert.Equal(("ABC Suppliers", "", "ABC Suppliers"), (e.VendorName, e.Payee, e.PaidTo));
        Assert.Equal("Mr. Landlord", Create(Payload(Category("Rent")) with { Payee = "Mr. Landlord" }).PaidTo);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("amount-negative")]
    [InlineData("tax_rate")]
    [InlineData("description")]
    [InlineData("payment_method")]
    public void Validation(string field)
    {
        var p = Payload(Category("Rent"));
        p = field switch
        {
            "amount" => p with { Amount = 0 },
            "amount-negative" => p with { Amount = -5 },
            "tax_rate" => p with { TaxRate = 101 },
            "description" => p with { Description = "   " },
            _ => p with { PaymentMethod = "BITCOIN" },
        };
        Assert.Equal(field.Split('-')[0], Assert.Throws<ValidationError>(() => Create(p)).Field);
    }

    [Fact]
    public void RejectsCustomerAsVendorAndInactiveCategory()
    {
        var rent = Category("Rent");
        Assert.Equal("VENDOR_NOT_FOUND", Assert.Throws<BusinessError>(() => Create(Payload(rent) with { VendorId = _t.Customer().Id })).Code);
        _t.Write(tx => ExpenseService.SetCategoryActive(tx, _t.Admin, rent.Id, false));
        Assert.Equal("CATEGORY_INACTIVE", Assert.Throws<BusinessError>(() => Create(Payload(rent))).Code);
    }

    [Fact]
    public void CancelKeepsRecord()
    {
        var e = Create(Payload(Category("Rent")));
        var c = _t.Write(tx => ExpenseService.Cancel(tx, _t.Admin, e.Id, "Entered twice"));
        Assert.Equal((DocumentStatus.Cancelled, "Entered twice"), (c.Status, c.CancelReason));
        Assert.Equal("ALREADY_CANCELLED", Assert.Throws<BusinessError>(() => _t.Write(tx => ExpenseService.Cancel(tx, _t.Admin, e.Id, "x"))).Code);
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("DELETE FROM expenses")));
    }

    [Fact]
    public void ListFilters()
    {
        var utilities = Category("Utilities");
        Create(Payload(Category("Rent")) with { Date = new DateOnly(2026, 9, 1) });
        Create(Payload(utilities) with { Description = "Electricity bill", VendorId = _t.Vendor().Id });
        int Count(ExpenseFilter f) => _t.Read(tx => ExpenseService.List(tx, f)).Count;
        Assert.Equal(["Electricity bill"], _t.Read(tx => ExpenseService.List(tx, new ExpenseFilter { CategoryId = utilities.Id })).Results.Select(e => e.Description));
        Assert.Equal(1, Count(new ExpenseFilter { Start = new DateOnly(2026, 9, 10) }));
        Assert.Equal(1, Count(new ExpenseFilter { Search = "electricity" }));
        Assert.Equal(1, Count(new ExpenseFilter { VendorId = _t.Vendor().Id }));
    }

    [Fact]
    public void TotalConstraintEnforcedByDatabase()
    {
        Create(Payload(Category("Rent")) with { Amount = 100 });
        Assert.Throws<SqliteException>(() => _t.Write(tx => tx.Execute("UPDATE expenses SET total_amount = 1")));
    }

    [Fact]
    public void Permissions()
    {
        var staff = _t.MakeUser(Role.Staff);
        var accountant = _t.MakeUser(Role.Accountant);
        var manager = _t.MakeUser(Role.Manager);
        var rent = Category("Rent");
        Assert.Throws<PermissionDeniedError>(() => Create(Payload(rent), staff));
        var e = Create(Payload(rent), accountant);
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => ExpenseService.Cancel(tx, accountant, e.Id, "x")));
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => ExpenseService.CreateCategory(tx, accountant, "Fuel")));
        _t.Write(tx => ExpenseService.Cancel(tx, manager, e.Id, "x"));
    }

    [Fact]
    public void CategoryCrud()
    {
        var rent = Category("Rent");
        var cat = _t.Write(tx => ExpenseService.CreateCategory(tx, _t.Admin, "  Internet  ", "ISP"));
        Assert.Equal(("Internet", 0), (cat.Name, cat.ExpenseCount));
        Assert.Equal("DUPLICATE_CATEGORY", Assert.Throws<BusinessError>(() => _t.Write(tx => ExpenseService.CreateCategory(tx, _t.Admin, "internet"))).Code);
        Assert.Equal("DUPLICATE_CATEGORY", Assert.Throws<BusinessError>(() => _t.Write(tx => ExpenseService.UpdateCategory(tx, _t.Admin, cat.Id, "RENT", ""))).Code);
        _t.Write(tx => ExpenseService.UpdateCategory(tx, _t.Admin, cat.Id, "Internet & Phone", ""));
        _t.Write(tx => { ExpenseService.DeleteCategory(tx, _t.Admin, cat.Id); return 0; });
        Create(Payload(rent));
        Assert.Equal("CATEGORY_HAS_EXPENSES", Assert.Throws<BusinessError>(() => _t.Write(tx => { ExpenseService.DeleteCategory(tx, _t.Admin, rent.Id); return 0; })).Code);
        Assert.False(_t.Write(tx => ExpenseService.SetCategoryActive(tx, _t.Admin, rent.Id, false)).IsActive);
        Assert.Equal(10, _t.Read(tx => ExpenseService.Categories(tx, isActive: true)).Count);
    }
}
