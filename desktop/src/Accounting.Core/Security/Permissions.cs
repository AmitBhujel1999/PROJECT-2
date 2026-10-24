namespace Accounting.Core.Security;

public enum Role
{
    Admin,
    Manager,
    Accountant,
    Staff,
}

/// <summary>Who is doing something. Every service call takes one, so permissions and the audit log always know.</summary>
public sealed record Actor(long? UserId, string Username, Role Role, string? IpAddress = null)
{
    /// <summary>The program itself (first-run setup, migrations). Has every permission.</summary>
    public static readonly Actor System = new(null, "", Role.Admin);
}

/// <summary>
/// Role-based permissions, the same matrix as backend/apps/users/permissions.py.
/// Every service that changes data checks it, so the desktop screens and the phone API
/// obey the same rules. Read permissions are checked by the screen or API endpoint that shows the data.
/// </summary>
public static class Permissions
{
    private static readonly Role[] All = [Role.Admin, Role.Manager, Role.Accountant, Role.Staff];
    private static readonly Role[] Mgmt = [Role.Admin, Role.Manager];
    private static readonly Role[] Finance = [Role.Admin, Role.Manager, Role.Accountant];
    private static readonly Role[] AdminOnly = [Role.Admin];

    public static readonly IReadOnlyDictionary<string, Role[]> Matrix = new Dictionary<string, Role[]>
    {
        ["dashboard.view"] = All,
        ["search.use"] = All,
        ["products.view"] = All,
        ["products.manage"] = Mgmt,
        ["parties.view"] = All,
        ["parties.create"] = All, // quick-add from sales/purchase entry
        ["parties.manage"] = Finance, // edit credit terms, activate/deactivate
        ["sales.view"] = All,
        ["sales.create"] = All,
        ["sales.cancel"] = Mgmt,
        ["purchases.view"] = All,
        ["purchases.create"] = Finance,
        ["purchases.cancel"] = Mgmt,
        ["documents.override_due_date"] = Finance,
        ["receipts.view"] = Finance,
        ["receipts.create"] = Finance,
        ["receipts.cancel"] = Mgmt,
        ["payments.view"] = Finance,
        ["payments.create"] = Finance,
        ["payments.cancel"] = Mgmt,
        ["ledgers.view"] = Finance,
        ["expenses.view"] = Finance,
        ["expenses.create"] = Finance,
        ["expenses.cancel"] = Mgmt,
        ["expenses.manage_categories"] = Mgmt,
        ["cashbank.list"] = All,
        ["cashbank.view"] = Finance,
        ["cashbank.manage"] = Mgmt,
        ["contra.create"] = Finance,
        ["contra.cancel"] = Mgmt,
        ["journal.view"] = Finance,
        ["journal.create"] = Finance,
        ["journal.cancel"] = Mgmt,
        ["journal.manage_ledgers"] = Mgmt,
        ["inventory.view"] = All,
        ["inventory.adjust"] = Mgmt,
        ["reports.view"] = Finance,
        ["users.manage"] = AdminOnly,
        ["audit.view"] = Mgmt,
        ["settings.view"] = All,
        ["settings.manage"] = AdminOnly,
    };

    public static bool Has(Actor actor, string permission) =>
        Matrix.TryGetValue(permission, out var roles) ? roles.Contains(actor.Role) : throw new ArgumentException($"Unknown permission '{permission}'.");

    public static void Require(Actor actor, string permission)
    {
        if (!Has(actor, permission))
            throw new PermissionDeniedError();
    }

    public static IReadOnlyList<string> For(Actor actor) => Matrix.Keys.Where(p => Has(actor, p)).Order().ToList();

    public static string ToCode(this Role role) => role.ToString().ToUpperInvariant();

    public static Role ParseRole(string code) => Enum.Parse<Role>(code, ignoreCase: true);
}
