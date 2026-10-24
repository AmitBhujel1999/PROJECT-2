using Accounting.Core;
using Accounting.Core.Audit;
using Accounting.Core.Security;

namespace Accounting.Tests;

public class UserTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void LoginWorksAndIsAudited()
    {
        var user = UserService.Login(_t.Db, "ADMIN1", "Sup3r-Secret!");
        Assert.Equal(Role.Admin, user.Role);
        Assert.NotNull(user.LastLogin);
        Assert.Contains(_t.Read(tx => AuditLog.Recent(tx)), e => e.Action == AuditAction.Login);
    }

    [Fact]
    public void WrongPasswordGivesGenericErrorAndIsAudited()
    {
        var ex = Assert.Throws<BusinessError>(() => UserService.Login(_t.Db, "admin1", "wrong"));
        Assert.Equal("INVALID_CREDENTIALS", ex.Code);
        Assert.Throws<BusinessError>(() => UserService.Login(_t.Db, "nobody", "wrong"));
        Assert.Equal(2, _t.Read(tx => AuditLog.Recent(tx)).Count(e => e.Action == AuditAction.LoginFailed));
    }

    [Fact]
    public void SecondFirstAdminRefused() =>
        Assert.Equal("ALREADY_SET_UP", Assert.Throws<BusinessError>(() => _t.Write(tx => UserService.CreateFirstAdmin(tx, "x", "Sup3r-Secret!"))).Code);

    [Fact]
    public void OnlyAdminsCreateUsersAndPasswordsAreChecked()
    {
        var manager = _t.MakeUser(Role.Manager);
        Assert.Throws<PermissionDeniedError>(() => _t.Write(tx => UserService.Create(tx, manager, "x", "Sup3r-Secret!", Role.Staff)));
        Assert.Equal("password", Assert.Throws<ValidationError>(() => _t.Write(tx => UserService.Create(tx, _t.Admin, "x", "short", Role.Staff))).Field);
        Assert.Equal("password", Assert.Throws<ValidationError>(() => _t.Write(tx => UserService.Create(tx, _t.Admin, "x", "1234567890", Role.Staff))).Field);
        Assert.Equal("username", Assert.Throws<ValidationError>(() => _t.Write(tx => UserService.Create(tx, _t.Admin, "Admin1", "Sup3r-Secret!", Role.Staff))).Field);
    }

    [Fact]
    public void ChangePassword()
    {
        _t.Write(tx => { UserService.ChangePassword(tx, _t.Admin, "Sup3r-Secret!", "N3w-Password!"); return 0; });
        Assert.Throws<BusinessError>(() => UserService.Login(_t.Db, "admin1", "Sup3r-Secret!"));
        UserService.Login(_t.Db, "admin1", "N3w-Password!");
    }

    [Fact]
    public void PermissionMatrixMatchesDjango()
    {
        Actor As(Role r) => new(1, "u", r);
        Assert.True(Permissions.Has(As(Role.Staff), "sales.create"));
        Assert.False(Permissions.Has(As(Role.Staff), "products.manage"));
        Assert.False(Permissions.Has(As(Role.Accountant), "inventory.adjust"));
        Assert.True(Permissions.Has(As(Role.Accountant), "parties.manage"));
        Assert.True(Permissions.Has(As(Role.Manager), "audit.view"));
        Assert.False(Permissions.Has(As(Role.Manager), "users.manage"));
        Assert.Equal(Permissions.Matrix.Count, Permissions.For(As(Role.Admin)).Count);
    }

    [Fact]
    public void AdminManagesUsersButNeverRemovesTheLastAdmin()
    {
        var staff = _t.MakeUser(Role.Staff);
        var updated = _t.Write(tx => UserService.Update(tx, _t.Admin, staff.UserId!.Value, "Ram", "", "", Role.Accountant, true));
        Assert.Equal((Role.Accountant, "Ram"), (updated.Role, updated.DisplayName));
        Assert.Contains(_t.Read(tx => AuditLog.Recent(tx)), e => e.Action == AuditAction.PermissionChange);
        Assert.Equal("SELF_LOCKOUT", Assert.Throws<BusinessError>(() =>
            _t.Write(tx => UserService.Update(tx, _t.Admin, _t.Admin.UserId!.Value, "", "", "", Role.Manager, true))).Code);
        _t.Write(tx => { UserService.ResetPassword(tx, _t.Admin, staff.UserId!.Value, "Fresh-Pass-1"); return 0; });
        UserService.Login(_t.Db, "user_staff", "Fresh-Pass-1");
        _t.Write(tx => UserService.Update(tx, _t.Admin, staff.UserId!.Value, "Ram", "", "", Role.Accountant, false));
        Assert.Throws<BusinessError>(() => UserService.Login(_t.Db, "user_staff", "Fresh-Pass-1"));
    }

    [Fact]
    public void PasswordHashRoundTrip()
    {
        var hash = PasswordHasher.Hash("abc12345!");
        Assert.True(PasswordHasher.Verify("abc12345!", hash));
        Assert.False(PasswordHasher.Verify("abc12345?", hash));
        Assert.NotEqual(hash, PasswordHasher.Hash("abc12345!"));
    }
}
