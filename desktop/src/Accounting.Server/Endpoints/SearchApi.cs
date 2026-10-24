using Accounting.Core.Common;
using Accounting.Core.Data;
using Microsoft.AspNetCore.Http;

namespace Accounting.Server.Endpoints;

/// <summary>
/// /api/search/?q= : products, customers, vendors, invoices, bills, receipts, payments and expenses
/// (top 5 each), or one type with paging (&amp;type=invoices). Only types the user may view.
/// </summary>
public static class SearchApi
{
    private sealed record Source(string Permission, string From, string Where, string OrderBy, Func<Row, object> Format);

    private static string Money(Row r, string column) => Format.Money(r.Money(column));

    private static readonly Dictionary<string, Source> Sources = new()
    {
        ["products"] = new("products.view", "products x", "x.name LIKE @q OR x.sku_code LIKE @q", "x.name",
            r => new { id = r.Long("id"), title = r.Str("name"), subtitle = $"SKU {r.Str("sku_code")}", url = $"/products/{r.Long("id")}", is_active = r.Bool("is_active") }),
        ["customers"] = new("parties.view", "parties x", "x.type = 'CUSTOMER' AND (x.name LIKE @q OR x.phone LIKE @q OR x.pan_vat_no LIKE @q OR x.email LIKE @q)", "x.name",
            r => Party(r, "customers")),
        ["vendors"] = new("parties.view", "parties x", "x.type = 'VENDOR' AND (x.name LIKE @q OR x.phone LIKE @q OR x.pan_vat_no LIKE @q OR x.email LIKE @q)", "x.name",
            r => Party(r, "vendors")),
        ["invoices"] = new("sales.view", "sales x JOIN parties p ON p.id = x.party_id", "x.number LIKE @q OR p.name LIKE @q", "x.date DESC, x.id DESC",
            r => new { id = r.Long("id"), title = r.Str("number"), subtitle = $"{r.Str("party_name")} · {r.Str("date")} · {Money(r, "total_amount")}", url = $"/sales/{r.Long("id")}", status = r.Str("payment_status") }),
        ["bills"] = new("purchases.view", "purchases x JOIN parties p ON p.id = x.party_id", "x.number LIKE @q OR x.vendor_bill_number LIKE @q OR p.name LIKE @q", "x.date DESC, x.id DESC",
            r => new { id = r.Long("id"), title = r.Str("number"), subtitle = $"{r.Str("party_name")} · {r.Str("date")} · {Money(r, "total_amount")}", url = $"/purchases/{r.Long("id")}", status = r.Str("payment_status") }),
        ["receipts"] = new("receipts.view", "customer_receipts x JOIN parties p ON p.id = x.party_id", "x.number LIKE @q OR x.reference_number LIKE @q OR p.name LIKE @q", "x.date DESC, x.id DESC",
            r => new { id = r.Long("id"), title = r.Str("number"), subtitle = $"{r.Str("party_name")} · {r.Str("date")} · {Money(r, "amount")}", url = $"/receipts/{r.Long("id")}" }),
        ["payments"] = new("payments.view", "vendor_payments x JOIN parties p ON p.id = x.party_id", "x.number LIKE @q OR x.reference_number LIKE @q OR p.name LIKE @q", "x.date DESC, x.id DESC",
            r => new { id = r.Long("id"), title = r.Str("number"), subtitle = $"{r.Str("party_name")} · {r.Str("date")} · {Money(r, "amount")}", url = $"/payments/{r.Long("id")}" }),
        ["expenses"] = new("expenses.view", "expenses x JOIN expense_categories c ON c.id = x.category_id LEFT JOIN parties p ON p.id = x.vendor_id",
            "x.number LIKE @q OR x.description LIKE @q OR x.payee LIKE @q OR x.reference_number LIKE @q OR p.name LIKE @q", "x.date DESC, x.id DESC",
            r => new { id = r.Long("id"), title = r.Str("number"), subtitle = $"{r.Str("category_name")} · {r.Str("description")} · {r.Str("date")} · {Money(r, "total_amount")}", url = $"/expenses/{r.Long("id")}", status = r.Str("status") }),
    };

    private static object Party(Row r, string route) => new
    {
        id = r.Long("id"), title = r.Str("name"),
        subtitle = string.Join(" · ", new[] { r.Str("phone"), r.Str("pan_vat_no").Length > 0 ? "PAN " + r.Str("pan_vat_no") : "" }.Where(x => x.Length > 0)),
        url = $"/{route}/{r.Long("id")}", is_active = r.Bool("is_active"),
    };

    private static string Columns(string key) => key switch
    {
        "invoices" or "bills" or "receipts" or "payments" => "x.*, p.name AS party_name",
        "expenses" => "x.*, c.name AS category_name",
        _ => "x.*",
    };

    public static IResult Search(Api a)
    {
        a.Require("search.use");
        var q = (a.Q("q") ?? "").Trim();
        if (q.Length < 2)
            return Api.Ok(new { query = q, groups = Array.Empty<object>() });
        var like = "%" + q[..Math.Min(q.Length, 100)].Replace("%", "").Replace("_", "") + "%";
        var allowed = Sources.Where(s => a.Can(s.Value.Permission)).ToDictionary();
        if (a.Q("type") is { } type)
        {
            if (!allowed.TryGetValue(type, out var source))
                return Api.Ok(new { query = q, groups = Array.Empty<object>() });
            var page = a.Page;
            var (count, rows) = a.Read(tx => (
                tx.Scalar<long>($"SELECT COUNT(*) FROM {source.From} WHERE {source.Where}", new { q = like }),
                tx.Rows($"SELECT {Columns(type)} FROM {source.From} WHERE {source.Where} ORDER BY {source.OrderBy} LIMIT @limit OFFSET @offset",
                    new { q = like, limit = page.Size, offset = page.Offset }).Select(source.Format).ToList()));
            return Api.Paged(new Page<object>(rows, (int)count, page.Page, page.Size), x => x,
                new Dictionary<string, object?> { ["type"] = type, ["query"] = q });
        }
        var groups = a.Read(tx => allowed.Select(s =>
        {
            var count = tx.Scalar<long>($"SELECT COUNT(*) FROM {s.Value.From} WHERE {s.Value.Where}", new { q = like });
            var rows = tx.Rows($"SELECT {Columns(s.Key)} FROM {s.Value.From} WHERE {s.Value.Where} ORDER BY {s.Value.OrderBy} LIMIT 5", new { q = like })
                .Select(s.Value.Format).ToList();
            return (s.Key, count, rows);
        }).Where(g => g.rows.Count > 0).Select(g => new { type = g.Key, count = g.count, results = g.rows }).ToList());
        return Api.Ok(new { query = q, groups });
    }
}
