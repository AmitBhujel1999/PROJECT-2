using System.Security.Cryptography;
using Accounting.Core.Audit;
using Accounting.Core.Data;

namespace Accounting.Core.Security;

public sealed record User(
    long Id, string Username, string FirstName, string LastName, string Email, string Phone, Role Role, bool IsActive,
    DateTime? LastLogin, DateTime DateJoined)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
    public string DisplayName => FullName.Length > 0 ? FullName : Username;

    public Actor ToActor(string? ipAddress = null) => new(Id, Username, Role, ipAddress);
}

/// <summary>Everything an administrator can set on an account. A null password keeps the current one.</summary>
public sealed record UserInput
{
    public string Username { get; init; } = "";
    public string? Password { get; init; }
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Email { get; init; } = "";
    public string Phone { get; init; } = "";
    public Role Role { get; init; } = Role.Staff;
    public bool IsActive { get; init; } = true;

    public static UserInput From(User u) => new()
    {
        Username = u.Username, FirstName = u.FirstName, LastName = u.LastName, Email = u.Email, Phone = u.Phone, Role = u.Role, IsActive = u.IsActive,
    };
}

public static class UserService
{
    public const int MinPasswordLength = 8;
    private const string Model = "users.User";

    public static bool AnyUsers(Tx tx) => tx.Scalar<long>("SELECT COUNT(*) FROM users") > 0;

    /// <summary>First run only: create the administrator when the company file has no users.</summary>
    public static User CreateFirstAdmin(Tx tx, string username, string password, string fullName = "")
    {
        if (AnyUsers(tx))
            throw new BusinessError("An administrator already exists.", "ALREADY_SET_UP", 409);
        return Insert(tx, Actor.System, new UserInput { Username = username, Password = password, FirstName = fullName, Role = Role.Admin });
    }

    public static User Create(Tx tx, Actor actor, string username, string password, Role role, string fullName = "") =>
        Create(tx, actor, new UserInput { Username = username, Password = password, FirstName = fullName, Role = role });

    public static User Create(Tx tx, Actor actor, UserInput input)
    {
        Permissions.Require(actor, "users.manage");
        return Insert(tx, actor, input);
    }

    private static User Insert(Tx tx, Actor actor, UserInput input)
    {
        var username = input.Username.Trim();
        ValidateUsername(tx, username, excludeId: null);
        if (string.IsNullOrEmpty(input.Password))
            throw new ValidationError("password", "Password is required for new users.");
        ValidatePassword(input.Password, username);
        var id = tx.Insert(
            """
            INSERT INTO users (username, first_name, last_name, email, phone, role, is_active, password_hash)
            VALUES (@username, @FirstName, @LastName, @Email, @Phone, @role, @IsActive, @hash)
            """,
            new { username, FirstName = input.FirstName.Trim(), LastName = input.LastName.Trim(), Email = input.Email.Trim(), Phone = input.Phone.Trim(),
                  role = input.Role.ToCode(), input.IsActive, hash = PasswordHasher.Hash(input.Password) });
        var user = Get(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Create, Model, id, username, after: AuditLog.Snapshot(user));
        return user;
    }

    private static void ValidateUsername(Tx tx, string username, long? excludeId)
    {
        if (username.Length == 0)
            throw new ValidationError("username", "This field may not be blank.");
        if (username.Length > 150 || username.Any(c => !(char.IsLetterOrDigit(c) || "@.+-_".Contains(c))))
            throw new ValidationError("username", "Enter a valid username. Use letters, numbers, and @/./+/-/_ only.");
        if (tx.Scalar<long>("SELECT COUNT(*) FROM users WHERE username = @username AND id IS NOT @excludeId", new { username, excludeId }) > 0)
            throw new ValidationError("username", "A user with that username already exists.");
    }

    /// <summary>
    /// The user, or null (and a LOGIN_FAILED audit row) when the credentials are wrong.
    /// The failed-login audit row is committed before the error is thrown by <see cref="Login"/>.
    /// </summary>
    public static User Login(Database db, string username, string password, string? ipAddress = null) =>
        db.Write(tx => TryAuthenticate(tx, username, password, ipAddress))
        ?? throw new BusinessError("Invalid username or password.", "INVALID_CREDENTIALS", 401);

    public static User? TryAuthenticate(Tx tx, string username, string password, string? ipAddress = null)
    {
        var row = tx.Row("SELECT id, password_hash, is_active FROM users WHERE username = @username", new { username = username.Trim() });
        if (row is null || !row.Bool("is_active") || !PasswordHasher.Verify(password, row.Str("password_hash")))
        {
            AuditLog.Record(tx, new Actor(null, "", Role.Staff, ipAddress), AuditAction.LoginFailed, Model, null, username.Trim());
            return null;
        }
        var id = row.Long("id");
        tx.Execute("UPDATE users SET last_login = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = @id", new { id });
        var user = Get(tx, id);
        AuditLog.Record(tx, user.ToActor(ipAddress), AuditAction.Login, Model, id, user.Username);
        return user;
    }

    /// <summary>The signed-in user changes their own name, email and phone.</summary>
    public static User UpdateProfile(Tx tx, Actor actor, string firstName, string lastName, string email, string phone)
    {
        var before = Get(tx, actor.UserId ?? throw new NotFoundError("User not found."));
        tx.Execute("UPDATE users SET first_name = @firstName, last_name = @lastName, email = @email, phone = @phone WHERE id = @id",
            new { id = before.Id, firstName = firstName.Trim(), lastName = lastName.Trim(), email = email.Trim(), phone = phone.Trim() });
        var after = Get(tx, before.Id);
        AuditLog.Record(tx, actor, AuditAction.Update, Model, before.Id, after.Username, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    public static void ChangePassword(Tx tx, Actor actor, string currentPassword, string newPassword)
    {
        var hash = tx.Scalar<string>("SELECT password_hash FROM users WHERE id = @id", new { id = actor.UserId })
                   ?? throw new NotFoundError("User not found.");
        if (!PasswordHasher.Verify(currentPassword, hash))
            throw new ValidationError("current_password", "Current password is incorrect.");
        ValidatePassword(newPassword, actor.Username, "new_password");
        tx.Execute("UPDATE users SET password_hash = @hash WHERE id = @id", new { hash = PasswordHasher.Hash(newPassword), id = actor.UserId });
        AuditLog.Record(tx, actor, AuditAction.PasswordChange, Model, actor.UserId, actor.Username);
    }

    /// <summary>
    /// Administrator changes an account. Admins cannot remove their own admin access, and the last
    /// active administrator cannot be demoted or deactivated.
    /// </summary>
    public static User Update(Tx tx, Actor actor, long id, UserInput input)
    {
        Permissions.Require(actor, "users.manage");
        var before = Get(tx, id);
        if (actor.UserId == id && (input.Role != Role.Admin || !input.IsActive))
            throw new BusinessError("You cannot remove your own administrator access.", "SELF_LOCKOUT");
        if ((input.Role != Role.Admin || !input.IsActive) && before is { Role: Role.Admin, IsActive: true }
            && tx.Scalar<long>("SELECT COUNT(*) FROM users WHERE role = 'ADMIN' AND is_active = 1") <= 1)
            throw new BusinessError("There must always be at least one active administrator.", "LAST_ADMIN");
        var username = input.Username.Trim();
        ValidateUsername(tx, username, id);
        if (!string.IsNullOrEmpty(input.Password))
        {
            ValidatePassword(input.Password, username);
            tx.Execute("UPDATE users SET password_hash = @hash WHERE id = @id", new { id, hash = PasswordHasher.Hash(input.Password) });
        }
        tx.Execute(
            """
            UPDATE users SET username = @username, first_name = @FirstName, last_name = @LastName, email = @Email, phone = @Phone,
                role = @role, is_active = @IsActive
            WHERE id = @id
            """,
            new { id, username, FirstName = input.FirstName.Trim(), LastName = input.LastName.Trim(), Email = input.Email.Trim(), Phone = input.Phone.Trim(),
                  role = input.Role.ToCode(), input.IsActive });
        var after = Get(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Update, Model, id, after.Username, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        if (before.Role != after.Role)
            AuditLog.Record(tx, actor, AuditAction.PermissionChange, Model, id, after.Username,
                AuditLog.Snapshot(new { role = before.Role.ToCode() }), AuditLog.Snapshot(new { role = after.Role.ToCode() }));
        if (before.IsActive != after.IsActive)
            AuditLog.Record(tx, actor, after.IsActive ? AuditAction.Activate : AuditAction.Deactivate, Model, id, after.Username);
        if (!after.IsActive)
            tx.Execute("DELETE FROM sessions WHERE user_id = @id", new { id });
        return after;
    }

    /// <summary>Shorter form used by the desktop screens.</summary>
    public static User Update(Tx tx, Actor actor, long id, string fullName, string email, string phone, Role role, bool isActive) =>
        Update(tx, actor, id, UserInput.From(Get(tx, id)) with { FirstName = fullName, LastName = "", Email = email, Phone = phone, Role = role, IsActive = isActive });

    /// <summary>Admin sets a new password for a user (e.g. a forgotten one).</summary>
    public static void ResetPassword(Tx tx, Actor actor, long id, string newPassword)
    {
        Permissions.Require(actor, "users.manage");
        var user = Get(tx, id);
        ValidatePassword(newPassword, user.Username);
        tx.Execute("UPDATE users SET password_hash = @hash WHERE id = @id", new { hash = PasswordHasher.Hash(newPassword), id });
        AuditLog.Record(tx, actor, AuditAction.PasswordChange, Model, id, user.Username);
    }

    public static User Get(Tx tx, long id) =>
        tx.Rows("SELECT * FROM users WHERE id = @id", new { id }).Select(Map).FirstOrDefault() ?? throw new NotFoundError("User not found.");

    public static IReadOnlyList<User> List(Tx tx) => tx.Rows("SELECT * FROM users ORDER BY username").Select(Map).ToList();

    private static readonly HashSet<string> CommonPasswords =
        ["password", "password1", "12345678", "123456789", "qwerty123", "iloveyou", "admin123", "welcome1", "letmein1", "abc12345"];

    /// <summary>Django's default rules: length, not all digits, not a common password, not the username.</summary>
    private static void ValidatePassword(string password, string username, string field = "password")
    {
        if (password.Length < MinPasswordLength)
            throw new ValidationError(field, $"This password is too short. It must contain at least {MinPasswordLength} characters.");
        if (password.All(char.IsDigit))
            throw new ValidationError(field, "This password is entirely numeric.");
        if (CommonPasswords.Contains(password.ToLowerInvariant()))
            throw new ValidationError(field, "This password is too common.");
        // Like Django's similarity check: mostly the username (e.g. "admin1" for "admin") is refused.
        if (username.Length > 2 && password.Contains(username, StringComparison.OrdinalIgnoreCase)
            && 2.0 * username.Length / (username.Length + password.Length) >= 0.7)
            throw new ValidationError(field, "The password is too similar to the username.");
    }

    private static User Map(Row r) => new(
        r.Long("id"), r.Str("username"), r.Str("first_name"), r.Str("last_name"), r.Str("email"), r.Str("phone"),
        Permissions.ParseRole(r.Str("role")), r.Bool("is_active"), r.NullableStr("last_login") is null ? null : r.Timestamp("last_login"),
        r.Timestamp("created_at"));

    // ------------------------------------------------------------------ sessions (built-in server)

    /// <summary>Start a sign-in session; returns the secret token for the session cookie.</summary>
    public static string StartSession(Tx tx, long userId, TimeSpan lifetime, string? ipAddress)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        tx.Execute("DELETE FROM sessions WHERE expires_at < strftime('%Y-%m-%dT%H:%M:%fZ', 'now')");
        tx.Execute("INSERT INTO sessions (token, user_id, expires_at, ip_address) VALUES (@token, @userId, @expires, @ipAddress)",
            new { token = HashToken(token), userId, expires = DateTime.UtcNow.Add(lifetime).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ipAddress });
        return token;
    }

    /// <summary>The active user behind a session token, or null when it is unknown, expired or the user is inactive.</summary>
    public static User? SessionUser(Tx tx, string token)
    {
        var id = tx.Scalar<long?>(
            "SELECT s.user_id FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.token = @token AND s.expires_at >= strftime('%Y-%m-%dT%H:%M:%fZ', 'now') AND u.is_active = 1",
            new { token = HashToken(token) });
        return id is { } userId ? Get(tx, userId) : null;
    }

    public static void EndSession(Tx tx, string token) => tx.Execute("DELETE FROM sessions WHERE token = @token", new { token = HashToken(token) });

    /// <summary>Only a hash of the token is stored, so a copied company file does not hand out live sessions.</summary>
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
