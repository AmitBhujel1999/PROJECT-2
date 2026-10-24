using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Security;

namespace Accounting.Core.CashBank;

public static class AccountKind
{
    public const string Cash = "CASH";
    public const string Bank = "BANK";
    public const string Wallet = "WALLET";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All = [Cash, Bank, Wallet, Other];

    /// <summary>Payment method stored on receipts/payments/expenses for each kind of account.</summary>
    public static string MethodFor(string kind) => kind switch
    {
        Cash => PaymentMethod.Cash,
        Bank => PaymentMethod.Bank,
        Wallet => PaymentMethod.Online,
        _ => PaymentMethod.Other,
    };

    public static string Display(string kind) => kind switch
    {
        Cash => "Cash",
        Bank => "Bank",
        Wallet => "Digital wallet",
        _ => "Other",
    };
}

/// <summary>A cash or bank account the business keeps money in.</summary>
public sealed record MoneyAccount
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Kind { get; init; } = AccountKind.Bank;
    public string AccountNumber { get; init; } = "";
    /// <summary>Balance before the first entry.</summary>
    public decimal OpeningBalance { get; init; }
    public string Notes { get; init; } = "";
    public bool IsActive { get; init; } = true;

    public override string ToString() => Name;
}

/// <summary>
/// Cash and bank accounts. Every receipt, vendor payment and expense names the account the
/// money came into or went out of; see <see cref="CashBook"/> for balances and books.
/// </summary>
public static class MoneyAccounts
{
    private const string Model = "cashbank.MoneyAccount";

    private static readonly Dictionary<string, string> DefaultNames = new()
    {
        [AccountKind.Cash] = "Cash in Hand",
        [AccountKind.Bank] = "Bank",
    };

    public static MoneyAccount Create(Tx tx, Actor actor, MoneyAccount input)
    {
        Permissions.Require(actor, "cashbank.manage");
        var data = Validate(tx, input, excludeId: null);
        var id = tx.Insert(
            """
            INSERT INTO money_accounts (name, kind, account_number, opening_balance, notes)
            VALUES (@Name, @Kind, @AccountNumber, @opening, @Notes)
            """, new { data.Name, data.Kind, data.AccountNumber, opening = Money.ToPaisa(data.OpeningBalance), data.Notes });
        var account = Get(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Create, Model, id, account.Name, after: AuditLog.Snapshot(account));
        return account;
    }

    public static MoneyAccount Update(Tx tx, Actor actor, long id, MoneyAccount input)
    {
        Permissions.Require(actor, "cashbank.manage");
        var before = Get(tx, id);
        var data = Validate(tx, input, excludeId: id);
        tx.Execute(
            """
            UPDATE money_accounts SET name = @Name, kind = @Kind, account_number = @AccountNumber, opening_balance = @opening,
                notes = @Notes, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @id
            """, new { id, data.Name, data.Kind, data.AccountNumber, opening = Money.ToPaisa(data.OpeningBalance), data.Notes });
        var after = Get(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Update, Model, id, after.Name, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    public static MoneyAccount SetActive(Tx tx, Actor actor, long id, bool active)
    {
        Permissions.Require(actor, "cashbank.manage");
        var before = Get(tx, id);
        tx.Execute("UPDATE money_accounts SET is_active = @active, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = @id",
            new { id, active });
        var after = Get(tx, id);
        AuditLog.Record(tx, actor, active ? AuditAction.Activate : AuditAction.Deactivate, Model, id, after.Name,
            AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    /// <summary>Physically delete only accounts that were never used.</summary>
    public static void Delete(Tx tx, Actor actor, long id)
    {
        Permissions.Require(actor, "cashbank.manage");
        var account = Get(tx, id);
        if (CashBook.IsUsed(tx, id))
            throw new BusinessError("This account has entries and cannot be deleted. Deactivate it instead.", "ACCOUNT_IN_USE", 409);
        AuditLog.Record(tx, actor, AuditAction.Delete, Model, id, account.Name, before: AuditLog.Snapshot(account));
        tx.Execute("DELETE FROM money_accounts WHERE id = @id", new { id });
    }

    public static MoneyAccount Get(Tx tx, long id) =>
        tx.Rows("SELECT * FROM money_accounts WHERE id = @id", new { id }).Select(Map).FirstOrDefault()
        ?? throw new BusinessError("Cash/bank account not found.", "ACCOUNT_NOT_FOUND");

    public static IReadOnlyList<MoneyAccount> List(Tx tx, bool? isActive = null) =>
        tx.Rows("SELECT * FROM money_accounts WHERE @isActive IS NULL OR is_active = @isActive ORDER BY name COLLATE NOCASE",
            new { isActive }).Select(Map).ToList();

    /// <summary>First active account of <paramref name="kind"/>; creates "Cash in Hand" / "Bank" when there is none.</summary>
    public static MoneyAccount Default(Tx tx, string kind)
    {
        var account = tx.Rows("SELECT * FROM money_accounts WHERE kind = @kind AND is_active = 1 ORDER BY id LIMIT 1", new { kind })
            .Select(Map).FirstOrDefault();
        if (account is not null)
            return account;
        tx.RequireWrite();
        var name = DefaultNames.GetValueOrDefault(kind, AccountKind.Display(kind));
        var existing = tx.Scalar<long?>("SELECT id FROM money_accounts WHERE name = @name", new { name });
        return Get(tx, existing ?? tx.Insert("INSERT INTO money_accounts (name, kind) VALUES (@name, @kind)", new { name, kind }));
    }

    /// <summary>
    /// Account and payment method for a receipt/payment/expense. With an account, the method follows
    /// the account's kind. Without one, cash goes to the cash account and everything else to the bank.
    /// </summary>
    public static (MoneyAccount Account, string Method) Resolve(Tx tx, long? accountId, string? paymentMethod = null)
    {
        if (accountId is { } id)
        {
            var account = Get(tx, id);
            if (!account.IsActive)
                throw new BusinessError($"Account {account.Name} is inactive.", "ACCOUNT_INACTIVE");
            return (account, AccountKind.MethodFor(account.Kind));
        }
        var method = string.IsNullOrEmpty(paymentMethod) ? PaymentMethod.Cash : paymentMethod;
        if (!PaymentMethod.All.Contains(method))
            throw new ValidationError("payment_method", $"\"{method}\" is not a valid choice.");
        return (Default(tx, method == PaymentMethod.Cash ? AccountKind.Cash : AccountKind.Bank), method);
    }

    private static MoneyAccount Validate(Tx tx, MoneyAccount input, long? excludeId)
    {
        var name = input.Name.Trim();
        if (name.Length == 0)
            throw new ValidationError("name", "Name is required.");
        if (!AccountKind.All.Contains(input.Kind))
            throw new ValidationError("kind", $"\"{input.Kind}\" is not a valid choice.");
        if (tx.Scalar<long>("SELECT COUNT(*) FROM money_accounts WHERE name = @name AND id IS NOT @excludeId", new { name, excludeId }) > 0)
            throw new BusinessError($"An account named {name} already exists.", "DUPLICATE_ACCOUNT");
        return input with { Name = name, AccountNumber = input.AccountNumber.Trim(), Notes = input.Notes.Trim() };
    }

    private static MoneyAccount Map(Row r) => new()
    {
        Id = r.Long("id"),
        Name = r.Str("name"),
        Kind = r.Str("kind"),
        AccountNumber = r.Str("account_number"),
        OpeningBalance = r.Money("opening_balance"),
        Notes = r.Str("notes"),
        IsActive = r.Bool("is_active"),
    };
}
