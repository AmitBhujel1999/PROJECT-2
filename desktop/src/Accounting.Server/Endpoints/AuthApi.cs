using System.Text.Json;
using Accounting.Core;
using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>Sign in / out, profile and password: /api/auth/*.</summary>
public static class AuthApi
{
    public static object UserJson(User u, bool withPermissions = true)
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = u.Id,
            ["username"] = u.Username,
            ["email"] = u.Email,
            ["first_name"] = u.FirstName,
            ["last_name"] = u.LastName,
            ["phone"] = u.Phone,
            ["role"] = u.Role.ToCode(),
            ["is_active"] = u.IsActive,
            ["last_login"] = Json.T(u.LastLogin),
            ["date_joined"] = Json.T(u.DateJoined),
            ["display_name"] = u.DisplayName,
        };
        if (withPermissions)
            data["permissions"] = Permissions.For(u.ToActor());
        return data;
    }

    public static void Map(RouteGroupBuilder api)
    {
        var anon = new AnonymousApi();
        api.MapGet("auth/csrf", (HttpContext h) =>
        {
            ApiServer.IssueCsrf(h);
            return Api.Ok(new { csrf = "set" });
        }).WithMetadata(anon);

        api.MapPost("auth/login", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            ApiServer.CheckLoginRate(h);
            var body = await a.BodyAsync();
            var username = body.ReqStr("username");
            var password = body.Str("password") ?? throw new ValidationError("password", "This field is required.");
            var companies = ApiServer.Companies(h);
            var slug = (string)h.Items["company"]!;
            var db = a.Db;
            if (body.Str("company") is { Length: > 0 } wanted)
            {
                db = companies.Get(wanted) ?? throw new BusinessError("Unknown company.", "UNKNOWN_COMPANY");
                slug = wanted;
            }
            var ip = h.Connection.RemoteIpAddress?.ToString();
            var (user, token) = db.Write(tx =>
            {
                var u = UserService.TryAuthenticate(tx, username, password, ip);
                return u is null ? (null, null) : (u, UserService.StartSession(tx, u.Id, ApiServer.SessionLifetime, ip));
            });
            if (user is null)
            {
                ApiServer.RecordFailedLogin(h);
                throw new BusinessError("Invalid username or password.", "INVALID_CREDENTIALS");
            }
            h.Response.Cookies.Append(ApiServer.SessionCookie, token!, new CookieOptions
                { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = ApiServer.SessionLifetime, Path = "/" });
            h.Response.Cookies.Append(ApiServer.CompanyCookie, slug, new CookieOptions
                { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), Path = "/" });
            return Api.Ok(UserJson(user), "Logged in successfully.");
        }).WithMetadata(anon);

        api.MapPost("auth/logout", (HttpContext h) =>
        {
            var a = Api.Of(h);
            var actor = a.Actor;
            a.Db.Write(tx =>
            {
                AuditLog.Record(tx, actor, AuditAction.Logout, "users.User", actor.UserId, actor.Username);
                UserService.EndSession(tx, h.Request.Cookies[ApiServer.SessionCookie]!);
            });
            h.Response.Cookies.Delete(ApiServer.SessionCookie, new CookieOptions { Path = "/" });
            return Api.Ok(null, "Logged out.");
        });

        api.MapGet("auth/me", (HttpContext h) => Api.Ok(UserJson(Api.Of(h).User!)));
        api.MapMethods("auth/me", ["PATCH", "PUT"], async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var u = a.User!;
            var email = b.StrOr("email", u.Email).Trim();
            if (email.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(email, out _))
                throw new ValidationError("email", "Enter a valid email address.");
            var updated = a.Write((tx, actor) => UserService.UpdateProfile(tx, actor, b.StrOr("first_name", u.FirstName), b.StrOr("last_name", u.LastName),
                email, b.StrOr("phone", u.Phone)));
            return Api.Ok(UserJson(updated), "Profile updated.");
        });

        api.MapPost("auth/password/change", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var current = b.Str("current_password") ?? throw new ValidationError("current_password", "This field is required.");
            var next = b.Str("new_password") ?? throw new ValidationError("new_password", "This field is required.");
            a.Write((tx, actor) => { UserService.ChangePassword(tx, actor, current, next); return 0; });
            return Api.Ok(null, "Password changed successfully.");
        });

        // No email is sent from a desktop program: an administrator resets passwords under Users.
        api.MapPost("auth/password/reset", () =>
            Api.Ok(null, "If an account exists for that email, a reset link has been sent.")).WithMetadata(anon);
        api.MapPost("auth/password/reset/confirm", IResult () =>
            throw new BusinessError("The reset link is invalid or has expired. Ask an administrator to reset your password.", "INVALID_TOKEN")).WithMetadata(anon);

        // Companies on the login screen, and creating a new one (approved by an admin of an existing company).
        api.MapGet("companies", (HttpContext h) =>
        {
            var registry = ApiServer.Companies(h);
            return Api.Ok(new
            {
                companies = registry.List().Select(c => new { slug = c.Slug, name = c.Name }), current = (string)h.Items["company"]!,
                setup = NeedsSetup(h),
            });
        }).WithMetadata(anon);

        api.MapPost("companies", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            ApiServer.CheckLoginRate(h);
            var b = await a.BodyAsync();
            var name = b.ReqStr("name").Trim();
            var adminUsername = b.ReqStr("admin_username");
            var adminPassword = b.Str("admin_password") ?? throw new ValidationError("admin_password", "This field is required.");
            var registry = ApiServer.Companies(h);
            if (b.Bool("setup") == true)
                return FirstSetup(h, b, registry.Main, name, adminUsername, adminPassword);
            var source = registry.Get(b.ReqStr("authorize_company")) ?? throw new BusinessError("Unknown company.", "UNKNOWN_COMPANY");
            var approver = source.Write(tx => UserService.TryAuthenticate(tx, b.ReqStr("authorize_username"), b.Str("authorize_password") ?? "", h.Connection.RemoteIpAddress?.ToString()));
            if (approver is not { Role: Role.Admin, IsActive: true })
            {
                ApiServer.RecordFailedLogin(h);
                throw new BusinessError("An admin username and password of the selected company is required.", "INVALID_CREDENTIALS");
            }
            var (slug, db) = registry.Create(name);
            try
            {
                db.Write(tx => SetUp(tx, b, name, adminUsername, adminPassword));
            }
            catch
            {
                registry.Discard(slug);
                throw;
            }
            return Api.Created(new { slug, name }, "Company created.");
        }).WithMetadata(anon);
    }

    /// <summary>
    /// The main company has no users yet: the first screen is "Set up your company". Only offered on the
    /// computer running the program, so a phone on the network cannot claim a fresh install.
    /// </summary>
    private static bool NeedsSetup(HttpContext h) =>
        IsLocal(h) && !ApiServer.Companies(h).Main.Read(UserService.AnyUsers);

    private static bool IsLocal(HttpContext h) => h.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);

    private static IResult FirstSetup(HttpContext h, JsonElement b, Database main, string name, string adminUsername, string adminPassword)
    {
        if (!IsLocal(h))
            throw new BusinessError("Set up the company on the computer running the program.", "SETUP_LOCAL_ONLY", 403);
        var admin = main.Write(tx => SetUp(tx, b, name, adminUsername, adminPassword));
        if (b.Bool("demo") == true)
            DemoData.Seed(main, admin.ToActor());
        return Api.Created(new { slug = CompanyRegistry.MainSlug, name }, "Company set up.");
    }

    /// <summary>First administrator and business details of a new, empty company file.</summary>
    private static User SetUp(Tx tx, JsonElement b, string name, string adminUsername, string adminPassword)
    {
        var admin = UserService.CreateFirstAdmin(tx, adminUsername, adminPassword, b.Str("admin_name") ?? "");
        if (b.Str("email") is { Length: > 0 } email)
            tx.Execute("UPDATE users SET email = @email WHERE id = @id", new { email, id = admin.Id });
        var settings = BusinessSettings.Get(tx);
        BusinessSettings.Save(tx, admin.ToActor(), settings with
        {
            BusinessName = name, Address = b.Str("address") ?? "", PanVatNo = b.Str("pan_vat_no") ?? "", Phone = b.Str("phone") ?? "", Email = b.Str("email") ?? "",
            DateSystem = b.Str("date_system") is "AD" or "BS" ? b.Str("date_system")! : settings.DateSystem,
        });
        return admin;
    }
}
