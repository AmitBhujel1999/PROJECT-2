using System.Text.Json;
using Accounting.Core;
using Accounting.Core.Common;
using Accounting.Core.Parties;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>/api/customers/, /api/vendors/ and /api/parties/: parties are deactivated, never deleted.</summary>
public static class PartiesApi
{
    public static object Json_(Party p, DateTime createdAt) => new
    {
        id = p.Id, type = p.Type.ToCode(), type_display = p.Type == PartyType.Customer ? "Customer" : "Vendor", name = p.Name,
        pan_vat_no = p.PanVatNo, phone = p.Phone, email = p.Email, address = p.Address,
        credit_terms = p.CreditTerms.ToCode(), credit_terms_display = p.CreditTerms.Display(), credit_days = p.CreditDays,
        credit_limit = Json.M(p.CreditLimit), opening_balance = Json.M(p.OpeningBalance), opening_side = p.OpeningSide, notes = p.Notes,
        is_active = p.IsActive, created_at = Json.T(createdAt), updated_at = Json.T(createdAt),
    };

    private static object Detail(Api a, long id) => a.Read(tx =>
    {
        var p = PartyService.Get(tx, id);
        return Json_(p, tx.Row("SELECT created_at FROM parties WHERE id = @id", new { id })!.Timestamp("created_at"));
    });

    private static readonly Dictionary<string, string> Ordering = new()
    {
        ["name"] = "name COLLATE NOCASE", ["created_at"] = "created_at", ["credit_limit"] = "credit_limit",
    };

    public static void Map(RouteGroupBuilder api)
    {
        foreach (var (route, type) in new (string, PartyType?)[] { ("customers", PartyType.Customer), ("vendors", PartyType.Vendor), ("parties", null) })
        {
            string Label(Party p) => p.Type == PartyType.Customer ? "Customer" : "Vendor";

            api.MapGet(route, (HttpContext h) =>
            {
                var a = Api.Of(h);
                a.Require("parties.view");
                var t = type?.ToCode() ?? a.Q("type");
                var args = new
                {
                    type = t, active = a.Bool("is_active"), terms = a.Q("credit_terms"), search = Core.Products.ProductService.LikePattern(a.Q("search")),
                    limit = a.Page.Size, offset = a.Page.Offset,
                };
                const string where =
                    """
                    WHERE (@type IS NULL OR type = @type) AND (@active IS NULL OR is_active = @active) AND (@terms IS NULL OR credit_terms = @terms)
                      AND (@search IS NULL OR name LIKE @search OR phone LIKE @search OR pan_vat_no LIKE @search OR email LIKE @search)
                    """;
                var page = a.Read(tx =>
                {
                    var count = tx.Scalar<long>($"SELECT COUNT(*) FROM parties {where}", args);
                    var rows = tx.Rows($"SELECT * FROM parties {where} ORDER BY {ProductsApi.OrderBy(a.Q("ordering"), Ordering, "name COLLATE NOCASE, id")} LIMIT @limit OFFSET @offset", args)
                        .Select(r => (PartyService.Map(r), r.Timestamp("created_at"))).ToList();
                    return new Page<(Party, DateTime)>(rows, (int)count, a.Page.Page, a.Page.Size);
                });
                return Api.Paged(page, x => Json_(x.Item1, x.Item2));
            });

            api.MapGet($"{route}/{{id:long}}", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                a.Require("parties.view");
                a.Read(tx => PartyService.Get(tx, id, type));
                return Api.Ok(Detail(a, id));
            });

            api.MapPost(route, async (HttpContext h) =>
            {
                var a = Api.Of(h);
                var b = await a.BodyAsync();
                var partyType = type ?? (b.Str("type") == "VENDOR" ? PartyType.Vendor : b.Str("type") == "CUSTOMER" ? PartyType.Customer
                    : throw new ValidationError("type", "This field is required."));
                var party = a.Write((tx, actor) => PartyService.Create(tx, actor, partyType, Input(b, null)));
                return Api.Created(Detail(a, party.Id), $"{Label(party)} {party.Name} created successfully.");
            });

            api.MapMethods($"{route}/{{id:long}}", ["PUT", "PATCH"], async (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var b = await a.BodyAsync();
                var current = a.Read(tx => PartyService.Get(tx, id, type));
                var party = a.Write((tx, actor) => PartyService.Update(tx, actor, id, Input(b, current)));
                return Api.Ok(Detail(a, id), $"{Label(party)} {party.Name} updated successfully.");
            });

            foreach (var (verb, active) in new[] { ("activate", true), ("deactivate", false) })
                api.MapPost($"{route}/{{id:long}}/{verb}", (HttpContext h, long id) =>
                {
                    var a = Api.Of(h);
                    a.Read(tx => PartyService.Get(tx, id, type));
                    var party = a.Write((tx, actor) => PartyService.SetActive(tx, actor, id, active));
                    return Api.Ok(Detail(a, id), $"{party.Name} {verb}d.");
                });
        }
    }

    /// <summary>Party fields from a request body; on update, missing fields keep their current value.</summary>
    public static PartyInput Input(JsonElement b, Party? current)
    {
        if (current is null && !b.Has("name"))
            throw new ValidationError("name", "This field is required.");
        var terms = b.Str("credit_terms") is { Length: > 0 } t
            ? Enum.GetValues<CreditTerms>().FirstOrDefault(c => c.ToCode() == t, (CreditTerms)(-1)) is var parsed && Enum.IsDefined(parsed)
                ? parsed : throw new ValidationError("credit_terms", $"\"{t}\" is not a valid choice.")
            : current?.CreditTerms ?? CreditTerms.Cash;
        if (terms == CreditTerms.Custom && current is null && b.Long("credit_days") is null)
            throw new ValidationError("credit_days", "Credit days are required for custom credit terms.");
        return new PartyInput
        {
            Name = b.StrOr("name", current?.Name ?? ""),
            PanVatNo = b.StrOr("pan_vat_no", current?.PanVatNo ?? ""),
            Phone = b.StrOr("phone", current?.Phone ?? ""),
            Email = b.StrOr("email", current?.Email ?? ""),
            Address = b.StrOr("address", current?.Address ?? ""),
            CreditTerms = terms,
            CreditDays = (int)(b.Long("credit_days") ?? current?.CreditDays ?? 0),
            CreditLimit = b.DecOr("credit_limit", current?.CreditLimit ?? 0),
            OpeningBalance = b.DecOr("opening_balance", current?.OpeningBalance ?? 0),
            OpeningSide = b.StrOr("opening_side", current?.OpeningSide ?? "DR") is { Length: > 0 } side ? side : "DR",
            Notes = b.StrOr("notes", current?.Notes ?? ""),
        };
    }
}
