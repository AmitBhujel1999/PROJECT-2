using Accounting.Core.Audit;
using Accounting.Core.CashBank;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Parties;
using Accounting.Core.Products;
using Accounting.Core.Security;

namespace Accounting.Core.Expenses;

/// <summary>Groups expenses for reporting (Rent, Salaries, ...). Used categories are deactivated, never deleted.</summary>
public sealed record ExpenseCategory(long Id, string Name, string Description, bool IsActive, int ExpenseCount)
{
    public override string ToString() => Name;
}

/// <summary>
/// A business expense paid when it is recorded (EXP-000001). The amount excludes tax; tax and total
/// are calculated here. Expenses are never deleted, only cancelled.
/// </summary>
public sealed record Expense
{
    public long Id { get; init; }
    public string Number { get; init; } = "";
    public DateOnly Date { get; init; }
    public long CategoryId { get; init; }
    public string CategoryName { get; init; } = "";
    public long? VendorId { get; init; }
    public string? VendorName { get; init; }
    /// <summary>Paid to, when not a registered vendor.</summary>
    public string Payee { get; init; } = "";
    public string Description { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal TaxRate { get; init; }
    public decimal TaxAmount { get; init; }
    public decimal TotalAmount { get; init; }
    public string PaymentMethod { get; init; } = Common.PaymentMethod.Cash;
    public long AccountId { get; init; }
    public string AccountName { get; init; } = "";
    public string ReferenceNumber { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Status { get; init; } = DocumentStatus.Active;
    public DateOnly? CancelledOn { get; init; }
    public string CancelReason { get; init; } = "";
    public DateTime CreatedAt { get; init; }

    public bool IsCancelled => Status == DocumentStatus.Cancelled;
    public string PaidTo => VendorName ?? Payee;

    public override string ToString() => Number;
}

public sealed record ExpenseInput
{
    public DateOnly Date { get; init; }
    public long CategoryId { get; init; }
    public long? VendorId { get; init; }
    public string Payee { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Excluding tax.</summary>
    public decimal Amount { get; init; }
    public decimal? TaxRate { get; init; }
    public string? PaymentMethod { get; init; }
    public long? AccountId { get; init; }
    public string ReferenceNumber { get; init; } = "";
    public string Notes { get; init; } = "";
}

public sealed record ExpenseTotals(decimal Amount, decimal TaxRate, decimal TaxAmount, decimal TotalAmount);

public sealed record ExpenseFilter
{
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public long? CategoryId { get; init; }
    public long? VendorId { get; init; }
    public long? AccountId { get; init; }
    /// <summary>Null = only active; "ALL" = both.</summary>
    public string? Status { get; init; }
    /// <summary>Matches number, description, payee, vendor, category or reference.</summary>
    public string? Search { get; init; }
}

/// <summary>Expenses and categories, ported from backend/apps/expenses/services.py.</summary>
public static class ExpenseService
{
    private const string Model = "expenses.Expense";
    private const string CategoryModel = "expenses.ExpenseCategory";

    public static ExpenseTotals Calculate(decimal amount, decimal? taxRate)
    {
        amount = Money.Round(amount);
        var rate = Money.Rate(taxRate ?? 0);
        var tax = Money.Round(amount * rate / Money.Hundred);
        return new ExpenseTotals(amount, rate, tax, Money.Round(amount + tax));
    }

    // ------------------------------------------------------------------ categories

    public static ExpenseCategory CreateCategory(Tx tx, Actor actor, string name, string description = "")
    {
        Permissions.Require(actor, "expenses.manage_categories");
        name = UniqueCategoryName(tx, name, excludeId: null);
        var id = tx.Insert("INSERT INTO expense_categories (name, description) VALUES (@name, @description)", new { name, description = description.Trim() });
        var category = GetCategory(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Create, CategoryModel, id, name, after: AuditLog.Snapshot(category));
        return category;
    }

    public static ExpenseCategory UpdateCategory(Tx tx, Actor actor, long id, string name, string description)
    {
        Permissions.Require(actor, "expenses.manage_categories");
        var before = GetCategory(tx, id);
        name = UniqueCategoryName(tx, name, excludeId: id);
        tx.Execute("UPDATE expense_categories SET name = @name, description = @description, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = @id",
            new { id, name, description = description.Trim() });
        var after = GetCategory(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Update, CategoryModel, id, name, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    public static ExpenseCategory SetCategoryActive(Tx tx, Actor actor, long id, bool active)
    {
        Permissions.Require(actor, "expenses.manage_categories");
        var before = GetCategory(tx, id);
        tx.Execute("UPDATE expense_categories SET is_active = @active, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = @id", new { id, active });
        var after = GetCategory(tx, id);
        AuditLog.Record(tx, actor, active ? AuditAction.Activate : AuditAction.Deactivate, CategoryModel, id, after.Name,
            AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    /// <summary>Physically delete only categories that were never used.</summary>
    public static void DeleteCategory(Tx tx, Actor actor, long id)
    {
        Permissions.Require(actor, "expenses.manage_categories");
        var category = GetCategory(tx, id);
        var used = tx.Scalar<long>(
            "SELECT EXISTS (SELECT 1 FROM expenses WHERE category_id = @id) OR EXISTS (SELECT 1 FROM journal_lines WHERE expense_category_id = @id)",
            new { id });
        if (used == 1)
            throw new BusinessError("This category has expenses and cannot be deleted. Deactivate it instead.", "CATEGORY_HAS_EXPENSES", 409);
        AuditLog.Record(tx, actor, AuditAction.Delete, CategoryModel, id, category.Name, before: AuditLog.Snapshot(category));
        tx.Execute("DELETE FROM expense_categories WHERE id = @id", new { id });
    }

    private static string UniqueCategoryName(Tx tx, string name, long? excludeId)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new ValidationError("name", "Name is required.");
        if (tx.Scalar<long>("SELECT COUNT(*) FROM expense_categories WHERE name = @name AND id IS NOT @excludeId", new { name, excludeId }) > 0)
            throw new BusinessError($"An expense category named {name} already exists.", "DUPLICATE_CATEGORY");
        return name;
    }

    private const string CategorySelect =
        "SELECT c.*, (SELECT COUNT(*) FROM expenses e WHERE e.category_id = c.id AND e.status = 'ACTIVE') AS expense_count FROM expense_categories c";

    public static ExpenseCategory GetCategory(Tx tx, long id) =>
        tx.Rows(CategorySelect + " WHERE c.id = @id", new { id }).Select(MapCategory).FirstOrDefault()
        ?? throw new BusinessError("Expense category not found.", "CATEGORY_NOT_FOUND");

    public static IReadOnlyList<ExpenseCategory> Categories(Tx tx, bool? isActive = null, string? search = null) =>
        tx.Rows(CategorySelect + " WHERE (@isActive IS NULL OR c.is_active = @isActive) AND (@search IS NULL OR c.name LIKE @search) ORDER BY c.name COLLATE NOCASE",
            new { isActive, search = ProductService.LikePattern(search) }).Select(MapCategory).ToList();

    private static ExpenseCategory MapCategory(Row r) =>
        new(r.Long("id"), r.Str("name"), r.Str("description"), r.Bool("is_active"), (int)r.Long("expense_count"));

    // ------------------------------------------------------------------ expenses

    public static Expense Create(Tx tx, Actor actor, ExpenseInput input)
    {
        Permissions.Require(actor, "expenses.create");
        tx.RequireWrite();
        if (input.Amount <= 0)
            throw new ValidationError("amount", "Ensure this value is greater than or equal to 0.01.");
        if (input.TaxRate is < 0 or > 100)
            throw new ValidationError("tax_rate", "Tax rate must be between 0 and 100.");
        var description = input.Description.Trim();
        if (description.Length == 0)
            throw new ValidationError("description", "This field may not be blank.");
        if (description.Length > 255)
            throw new ValidationError("description", "Ensure this field has no more than 255 characters.");
        if (input.PaymentMethod is { } m && !PaymentMethod.All.Contains(m))
            throw new ValidationError("payment_method", $"\"{m}\" is not a valid choice.");
        if (input.ReferenceNumber.Length > 60)
            throw new ValidationError("reference_number", "Ensure this field has no more than 60 characters.");
        var category = GetCategory(tx, input.CategoryId);
        if (!category.IsActive)
            throw new BusinessError($"Expense category {category.Name} is inactive.", "CATEGORY_INACTIVE");
        Party? vendor = null;
        if (input.VendorId is { } vendorId)
        {
            vendor = tx.Rows("SELECT * FROM parties WHERE id = @vendorId AND type = 'VENDOR'", new { vendorId }).Select(PartyService.Map).FirstOrDefault()
                     ?? throw new BusinessError("Vendor not found.", "VENDOR_NOT_FOUND");
            if (!vendor.IsActive)
                throw new BusinessError($"Vendor {vendor.Name} is inactive.", "PARTY_INACTIVE");
        }
        var totals = Calculate(input.Amount, input.TaxRate);
        var (account, method) = MoneyAccounts.Resolve(tx, input.AccountId, input.PaymentMethod);
        var id = tx.Insert(
            """
            INSERT INTO expenses (number, date, category_id, vendor_id, payee, description, amount, tax_rate, tax_amount, total_amount,
                                  payment_method, account_id, reference_number, notes, created_by)
            VALUES (@number, @date, @categoryId, @vendorId, @payee, @description, @amount, @taxRate, @tax, @total,
                    @method, @accountId, @referenceNumber, @notes, @UserId)
            """, new
            {
                number = Sequences.Next(tx, Sequences.Expense), date = input.Date, categoryId = category.Id, vendorId = vendor?.Id,
                payee = vendor is null ? input.Payee.Trim() : "", description, amount = Money.ToPaisa(totals.Amount),
                taxRate = Money.ToPaisa(totals.TaxRate), tax = Money.ToPaisa(totals.TaxAmount), total = Money.ToPaisa(totals.TotalAmount),
                method, accountId = account.Id, referenceNumber = input.ReferenceNumber.Trim(), notes = input.Notes.Trim(), actor.UserId,
            });
        var expense = Get(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Create, Model, id, expense.Number, after: AuditLog.Snapshot(expense));
        return expense;
    }

    public static Expense Cancel(Tx tx, Actor actor, long id, string reason)
    {
        Permissions.Require(actor, "expenses.cancel");
        var before = Get(tx, id);
        if (before.IsCancelled)
            throw new BusinessError($"{before.Number} is already cancelled.", "ALREADY_CANCELLED");
        if (string.IsNullOrWhiteSpace(reason))
            throw new BusinessError("A cancellation reason is required.", "REASON_REQUIRED");
        tx.Execute(
            """
            UPDATE expenses SET status = 'CANCELLED', cancelled_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), cancelled_on = @today,
                cancelled_by = @UserId, cancel_reason = @reason, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @id
            """, new { id, today = tx.Today, actor.UserId, reason = reason.Trim() });
        var after = Get(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Cancel, Model, id, after.Number, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    private const string Select =
        """
        SELECT e.*, c.name AS category_name, v.name AS vendor_name, a.name AS account_name FROM expenses e
        JOIN expense_categories c ON c.id = e.category_id
        JOIN money_accounts a ON a.id = e.account_id
        LEFT JOIN parties v ON v.id = e.vendor_id
        """;

    public static Expense Get(Tx tx, long id) =>
        tx.Rows(Select + " WHERE e.id = @id", new { id }).Select(Map).FirstOrDefault() ?? throw new NotFoundError("Expense not found.");

    private const string Where =
        """
        WHERE (@start IS NULL OR e.date >= @start) AND (@end IS NULL OR e.date <= @end)
          AND (@categoryId IS NULL OR e.category_id = @categoryId) AND (@vendorId IS NULL OR e.vendor_id = @vendorId)
          AND (@accountId IS NULL OR e.account_id = @accountId)
          AND (@status = 'ALL' OR e.status = @status)
          AND (@search IS NULL OR e.number LIKE @search OR e.description LIKE @search OR e.payee LIKE @search
               OR v.name LIKE @search OR c.name LIKE @search OR e.reference_number LIKE @search)
        """;

    private static object Args(ExpenseFilter f, PageRequest page) => new
    {
        start = f.Start, end = f.End, categoryId = f.CategoryId, vendorId = f.VendorId, accountId = f.AccountId,
        status = f.Status ?? DocumentStatus.Active, search = ProductService.LikePattern(f.Search), limit = page.Size, offset = page.Offset,
    };

    /// <summary>Expenses newest first. The default filter shows only active ones; Status = "ALL" includes cancelled.</summary>
    public static Page<Expense> List(Tx tx, ExpenseFilter filter, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        var args = Args(filter, page);
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM ({Select} {Where})", args);
        var rows = tx.Rows($"{Select} {Where} ORDER BY e.date DESC, e.id DESC LIMIT @limit OFFSET @offset", args).Select(Map).ToList();
        return new Page<Expense>(rows, (int)count, page.Page, page.Size);
    }

    /// <summary>Every expense matching the filter (for reports and exports).</summary>
    public static IReadOnlyList<Expense> All(Tx tx, ExpenseFilter filter) =>
        tx.Rows($"{Select} {Where} ORDER BY e.date, e.id", Args(filter, new PageRequest(1, 100))).Select(Map).ToList();

    private static Expense Map(Row r) => new()
    {
        Id = r.Long("id"),
        Number = r.Str("number"),
        Date = r.Date("date"),
        CategoryId = r.Long("category_id"),
        CategoryName = r.Str("category_name"),
        VendorId = r.NullableLong("vendor_id"),
        VendorName = r.NullableStr("vendor_name"),
        Payee = r.Str("payee"),
        Description = r.Str("description"),
        Amount = r.Money("amount"),
        TaxRate = r.Rate("tax_rate"),
        TaxAmount = r.Money("tax_amount"),
        TotalAmount = r.Money("total_amount"),
        PaymentMethod = r.Str("payment_method"),
        AccountId = r.Long("account_id"),
        AccountName = r.Str("account_name"),
        ReferenceNumber = r.Str("reference_number"),
        Notes = r.Str("notes"),
        Status = r.Str("status"),
        CancelledOn = r.NullableDate("cancelled_on"),
        CancelReason = r.Str("cancel_reason"),
        CreatedAt = r.Timestamp("created_at"),
    };
}
