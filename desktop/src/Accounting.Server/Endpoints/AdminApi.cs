using Accounting.Core;
using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>Business settings, users and roles, audit log.</summary>
public static class AdminApi
{
    private static readonly Dictionary<Role, string> RoleDescriptions = new()
    {
        [Role.Admin] = "Full access including users, roles and business settings.",
        [Role.Manager] = "All transactions, cancellations, expense categories, stock adjustments, products, reports and audit log.",
        [Role.Accountant] = "Sales, purchases, receipts, payments, expenses, ledgers, aging and reports.",
        [Role.Staff] = "Day-to-day sales entry, product/party lookup and stock view.",
    };

    private static object Roles() => Enum.GetValues<Role>().Select(r => new
    {
        role = r.ToCode(),
        label = r.ToString(),
        description = RoleDescriptions[r],
        permissions = Permissions.Matrix.Where(p => p.Value.Contains(r)).Select(p => p.Key).Order().ToList(),
    });

    private static object SettingsJson(Api a)
    {
        var s = a.Settings;
        var updated = a.Read(tx => tx.Scalar<string>("SELECT updated_at FROM business_settings WHERE id = 1"));
        return new
        {
            business_name = s.BusinessName, address = s.Address, pan_vat_no = s.PanVatNo, phone = s.Phone, email = s.Email,
            currency_code = s.CurrencyCode, currency_symbol = s.CurrencySymbol, tax_label = s.TaxLabel, default_tax_rate = Json.R(s.DefaultTaxRate),
            invoice_footer = s.InvoiceFooter, date_system = s.DateSystem,
            updated_at = updated is null ? null : Json.T(DateTime.Parse(updated, null, System.Globalization.DateTimeStyles.AdjustToUniversal)),
        };
    }

    private static object AuditJson(AuditEntry e) => new
    {
        id = e.Id, user = e.UserId, username = e.Username, action = e.Action, model_name = e.ModelName, object_id = e.ObjectId,
        object_repr = e.ObjectRepr, timestamp = Json.T(e.Timestamp), ip_address = e.IpAddress,
        before_data = e.BeforeData is null ? null : (object?)System.Text.Json.JsonDocument.Parse(e.BeforeData).RootElement,
        after_data = e.AfterData is null ? null : (object?)System.Text.Json.JsonDocument.Parse(e.AfterData).RootElement,
    };

    public static void Map(RouteGroupBuilder api)
    {
        // ------------------------------------------------------------------ settings
        api.MapGet("settings", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("settings.view");
            return Api.Ok(SettingsJson(a));
        });
        api.MapMethods("settings", ["PUT", "PATCH"], async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var s = a.Settings;
            var next = s with
            {
                BusinessName = b.StrOr("business_name", s.BusinessName), Address = b.StrOr("address", s.Address), PanVatNo = b.StrOr("pan_vat_no", s.PanVatNo),
                Phone = b.StrOr("phone", s.Phone), Email = b.StrOr("email", s.Email), CurrencyCode = b.StrOr("currency_code", s.CurrencyCode),
                CurrencySymbol = b.StrOr("currency_symbol", s.CurrencySymbol), TaxLabel = b.StrOr("tax_label", s.TaxLabel),
                DefaultTaxRate = b.DecOr("default_tax_rate", s.DefaultTaxRate), InvoiceFooter = b.StrOr("invoice_footer", s.InvoiceFooter),
                DateSystem = b.StrOr("date_system", s.DateSystem),
            };
            a.Write((tx, actor) => BusinessSettings.Save(tx, actor, next));
            return Api.Ok(SettingsJson(a), "Settings saved.");
        });

        // ------------------------------------------------------------------ users & roles
        api.MapGet("roles", () => Api.Ok(Roles()));
        api.MapGet("users/roles", (HttpContext h) =>
        {
            Api.Of(h).Require("users.manage");
            return Api.Ok(Roles());
        });
        api.MapGet("users", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("users.manage");
            var search = a.Q("search")?.ToLowerInvariant();
            var role = a.Q("role");
            var active = a.Bool("is_active");
            var users = a.Read(UserService.List)
                .Where(u => search is null || new[] { u.Username, u.Email, u.FirstName, u.LastName }.Any(f => f.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .Where(u => role is null || u.Role.ToCode() == role)
                .Where(u => active is null || u.IsActive == active)
                .ToList();
            return a.PagedList(users, u => AuthApi.UserJson(u, withPermissions: false));
        });
        api.MapGet("users/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("users.manage");
            return Api.Ok(AuthApi.UserJson(a.Read(tx => UserService.Get(tx, id)), withPermissions: false));
        });
        api.MapPost("users", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var input = new UserInput
            {
                Username = b.ReqStr("username"), Password = b.Str("password"), FirstName = b.Str("first_name") ?? "", LastName = b.Str("last_name") ?? "",
                Email = b.Str("email") ?? "", Phone = b.Str("phone") ?? "", Role = ParseRole(b.Str("role") ?? "STAFF"), IsActive = b.Bool("is_active") ?? true,
            };
            var user = a.Write((tx, actor) => UserService.Create(tx, actor, input));
            return Api.Created(AuthApi.UserJson(user, withPermissions: false), "User created successfully.");
        });
        api.MapMethods("users/{id:long}", ["PUT", "PATCH"], async (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("users.manage");
            var b = await a.BodyAsync();
            var current = a.Read(tx => UserService.Get(tx, id));
            var input = UserInput.From(current) with
            {
                Username = b.StrOr("username", current.Username), Password = b.Str("password"), FirstName = b.StrOr("first_name", current.FirstName),
                LastName = b.StrOr("last_name", current.LastName), Email = b.StrOr("email", current.Email), Phone = b.StrOr("phone", current.Phone),
                Role = b.Has("role") ? ParseRole(b.Str("role") ?? "") : current.Role, IsActive = b.BoolOr("is_active", current.IsActive),
            };
            var user = a.Write((tx, actor) => UserService.Update(tx, actor, id, input));
            return Api.Ok(AuthApi.UserJson(user, withPermissions: false), "User updated successfully.");
        });

        // ------------------------------------------------------------------ audit log
        api.MapGet("audit-logs", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("audit.view");
            var page = a.Read(tx => AuditLog.List(tx, new AuditFilter
            {
                Action = a.Q("action"), ModelName = a.Q("model_name"), ObjectId = a.Q("object_id"), UserId = a.Int("user"),
                Start = a.Date("start_date"), End = a.Date("end_date"), Search = a.Q("search"),
            }, a.Page));
            return Api.Paged(page, AuditJson);
        });
        api.MapGet("audit-logs/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("audit.view");
            var entry = a.Read(tx => AuditLog.Get(tx, id));
            return Api.Ok(AuditJson(entry));
        });
    }

    private static Role ParseRole(string code) =>
        Enum.GetValues<Role>().FirstOrDefault(r => r.ToCode() == code, (Role)(-1)) is var r && Enum.IsDefined(r)
            ? r : throw new ValidationError("role", "Invalid role.");
}
