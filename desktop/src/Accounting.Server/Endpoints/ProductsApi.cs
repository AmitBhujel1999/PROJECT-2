using System.Text.Json;
using Accounting.Core;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Inventory;
using Accounting.Core.Products;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>/api/products/: list, detail, create, update, delete, activate/deactivate (bulk: see BulkApi).</summary>
public static class ProductsApi
{
    public static object Json_(Product p, decimal? currentStock) => new
    {
        id = p.Id, name = p.Name, sku_code = p.SkuCode, unit = p.Unit.ToCode(), unit_display = p.Unit.Display(), description = p.Description, hs_code = p.HsCode,
        opening_stock = Json.Q(p.OpeningStock), opening_rate = Json.M(p.OpeningRate), opening_amount = Json.M(p.OpeningAmount),
        reorder_level = Json.Q(p.ReorderLevel), purchase_price = Json.M(p.PurchasePrice), selling_price = Json.M(p.SellingPrice),
        tax_rate = Json.R(p.TaxRate), is_active = p.IsActive, current_stock = currentStock is { } s ? Json.Q(s) : null,
        created_at = Json.T(p.CreatedAt), updated_at = Json.T(p.CreatedAt),
    };

    private static object Detail(Tx tx, long id)
    {
        var p = ProductService.Get(tx, id);
        return Json_(p, InventoryService.CalculateStock(tx, id));
    }

    private static readonly Dictionary<string, string> Ordering = new()
    {
        ["name"] = "p.name COLLATE NOCASE", ["sku_code"] = "p.sku_code", ["selling_price"] = "p.selling_price", ["purchase_price"] = "p.purchase_price",
        ["created_at"] = "p.created_at", ["current_stock"] = "current_stock",
    };

    /// <summary>"ordering=-selling_price" style sorting, limited to known columns.</summary>
    public static string OrderBy(string? ordering, IReadOnlyDictionary<string, string> allowed, string fallback)
    {
        if (ordering is null)
            return fallback;
        var parts = ordering.Split(',').Select(o => o.Trim()).Where(o => allowed.ContainsKey(o.TrimStart('-')))
            .Select(o => allowed[o.TrimStart('-')] + (o.StartsWith('-') ? " DESC" : "")).ToList();
        return parts.Count > 0 ? string.Join(", ", parts) + ", " + fallback : fallback;
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("products", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("products.view");
            var args = new
            {
                search = ProductService.LikePattern(a.Q("search")), active = a.Bool("is_active"), unit = a.Q("unit"),
                limit = a.Page.Size, offset = a.Page.Offset,
            };
            const string where =
                """
                WHERE (@active IS NULL OR p.is_active = @active) AND (@unit IS NULL OR p.unit = @unit)
                  AND (@search IS NULL OR p.name LIKE @search OR p.sku_code LIKE @search)
                """;
            const string stock = "COALESCE((SELECT running_balance FROM stock_movements m WHERE m.product_id = p.id ORDER BY m.id DESC LIMIT 1), 0)";
            var page = a.Read(tx =>
            {
                var count = tx.Scalar<long>($"SELECT COUNT(*) FROM products p {where}", args);
                var rows = tx.Rows($"SELECT p.*, {stock} AS current_stock FROM products p {where} ORDER BY {OrderBy(a.Q("ordering"), Ordering, "p.name COLLATE NOCASE, p.id")} LIMIT @limit OFFSET @offset", args)
                    .Select(r => (ProductService.Map(r), r.Qty("current_stock"))).ToList();
                return new Page<(Product, decimal)>(rows, (int)count, a.Page.Page, a.Page.Size);
            });
            return Api.Paged(page, x => Json_(x.Item1, x.Item2));
        });

        api.MapGet("products/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("products.view");
            return Api.Ok(a.Read(tx => Detail(tx, id)));
        });

        api.MapPost("products", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var input = Input(b, null);
            var product = a.Write((tx, actor) => ProductService.Create(tx, actor, input));
            return Api.Created(a.Read(tx => Detail(tx, product.Id)), $"Product {product.Name} created successfully.");
        });

        api.MapMethods("products/{id:long}", ["PUT", "PATCH"], async (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var current = a.Read(tx => ProductService.Get(tx, id));
            var product = a.Write((tx, actor) => ProductService.Update(tx, actor, id, Input(b, current)));
            return Api.Ok(a.Read(tx => Detail(tx, id)), $"Product {product.Name} updated successfully.");
        });

        api.MapDelete("products/{id:long}", (HttpContext h, long id) =>
        {
            Api.Of(h).Write((tx, actor) => { ProductService.Delete(tx, actor, id); return 0; });
            return Api.Ok(null, "Product deleted.");
        });

        foreach (var (verb, active) in new[] { ("activate", true), ("deactivate", false) })
            api.MapPost($"products/{{id:long}}/{verb}", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var p = a.Write((tx, actor) => ProductService.SetActive(tx, actor, id, active));
                return Api.Ok(a.Read(tx => Detail(tx, id)), $"{p.Name} {verb}d.");
            });
    }

    /// <summary>Product fields from a request body; on update, missing fields keep their current value.</summary>
    public static ProductInput Input(JsonElement b, Product? current)
    {
        if (current is null && !b.Has("name"))
            throw new ValidationError("name", "This field is required.");
        decimal NonNegative(string field, decimal fallback)
        {
            var v = b.Has(field) ? b.Dec(field) ?? 0 : fallback;
            return v >= 0 ? v : throw new ValidationError(field, "Ensure this value is greater than or equal to 0.");
        }
        var unit = b.Str("unit") is { Length: > 0 } u
            ? Enum.TryParse<Unit>(u, ignoreCase: true, out var parsed) ? parsed : throw new ValidationError("unit", $"\"{u}\" is not a valid choice.")
            : current?.Unit ?? Unit.Pcs;
        return new ProductInput
        {
            Name = b.StrOr("name", current?.Name ?? ""),
            SkuCode = b.StrOr("sku_code", current?.SkuCode ?? ""),
            Unit = unit,
            Description = b.StrOr("description", current?.Description ?? ""),
            HsCode = b.StrOr("hs_code", current?.HsCode ?? ""),
            OpeningStock = NonNegative("opening_stock", current?.OpeningStock ?? 0),
            OpeningRate = b.Has("opening_rate") ? NonNegative("opening_rate", 0) : current?.OpeningRate,
            ReorderLevel = NonNegative("reorder_level", current?.ReorderLevel ?? 0),
            PurchasePrice = NonNegative("purchase_price", current?.PurchasePrice ?? 0),
            SellingPrice = NonNegative("selling_price", current?.SellingPrice ?? 0),
            TaxRate = b.Has("tax_rate") ? b.Dec("tax_rate") : current?.TaxRate,
        };
    }
}
