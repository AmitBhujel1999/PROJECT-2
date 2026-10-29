using System.Text.Json;
using Accounting.Core;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Products;
using Accounting.Core.Reports;
using Accounting.Core.Trade;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>/api/sales/ and /api/purchases/: list, detail, create, calculate (preview), cancel, PDF.</summary>
public static class TradeApi
{
    private static string Number(TradeKind k) => k.IsSale ? "invoice_number" : "bill_number";
    private static string PartyKey(TradeKind k) => k.IsSale ? "customer" : "vendor";

    public static Dictionary<string, object?> ListJson(TradeKind kind, TradeDocument d, int? items = null, decimal? quantity = null)
    {
        var party = PartyKey(kind);
        var data = new Dictionary<string, object?>
        {
            ["id"] = d.Id,
            [Number(kind)] = d.Number,
        };
        if (!kind.IsSale)
            data["vendor_bill_number"] = d.VendorBillNumber;
        data["date"] = Json.D(d.Date);
        data["due_date"] = Json.D(d.DueDate);
        data[party] = d.PartyId;
        data[party + "_name"] = d.PartyName;
        data["subtotal"] = Json.M(d.Subtotal);
        data["item_discount_total"] = Json.M(d.ItemDiscountTotal);
        data["discount_type"] = d.DiscountType.ToCode();
        data["discount_value"] = Json.M(d.DiscountValue);
        data["discount_amount"] = Json.M(d.DiscountAmount);
        data["total_discount"] = Json.M(d.TotalDiscount);
        data["taxable_amount"] = Json.M(d.TaxableAmount);
        data["tax_amount"] = Json.M(d.TaxAmount);
        data["total_amount"] = Json.M(d.TotalAmount);
        data["amount_paid"] = Json.M(d.AmountPaid);
        data["balance_due"] = Json.M(d.BalanceDue);
        data["payment_status"] = d.PaymentStatus;
        data["status"] = d.Status;
        data["item_count"] = items;
        data["total_quantity"] = quantity is { } q ? Json.Q(q) : null;
        data["created_at"] = Json.T(d.CreatedAt);
        return data;
    }

    public static Dictionary<string, object?> DetailJson(Tx tx, TradeKind kind, long id)
    {
        var d = TradeService.Get(tx, kind, id);
        var data = ListJson(kind, d, d.Lines.Count, d.Lines.Sum(l => l.Quantity));
        var party = PartyService.Get(tx, d.PartyId);
        var prefix = PartyKey(kind);
        data["items"] = d.Lines.Select(l => new Dictionary<string, object?>
        {
            ["id"] = l.Id, ["product"] = l.ProductId, ["product_name"] = l.ProductName, ["sku_code"] = l.SkuCode, ["unit"] = l.Unit,
            ["quantity"] = Json.Q(l.Quantity), [kind.IsSale ? "unit_price" : "unit_cost"] = Json.M(l.UnitPrice), ["gross_amount"] = Json.M(l.GrossAmount),
            ["discount_type"] = l.DiscountType.ToCode(), ["discount_value"] = Json.M(l.DiscountValue), ["discount_amount"] = Json.M(l.DiscountAmount),
            ["invoice_discount_share"] = Json.M(l.InvoiceDiscountShare), ["taxable_amount"] = Json.M(l.TaxableAmount), ["tax_rate"] = Json.R(l.TaxRate),
            ["tax_amount"] = Json.M(l.TaxAmount), [kind.IsSale ? "total_price" : "total_cost"] = Json.M(l.LineTotal),
        }).ToList();
        data[prefix + "_pan_vat_no"] = party.PanVatNo;
        data[prefix + "_address"] = party.Address;
        data[prefix + "_phone"] = party.Phone;
        data["notes"] = d.Notes;
        data["created_by_name"] = d.CreatedByName;
        var extra = tx.Row($"SELECT d.updated_at, u.username AS cancelled_by_name FROM {kind.Table} d LEFT JOIN users u ON u.id = d.cancelled_by WHERE d.id = @id", new { id })!;
        data["cancelled_at"] = Json.T(d.CancelledAt);
        data["cancelled_by_name"] = extra.NullableStr("cancelled_by_name");
        data["cancel_reason"] = d.CancelReason;
        data["updated_at"] = Json.T(extra.Timestamp("updated_at"));
        var cfg = AccountConfig.For(kind);
        data["allocations"] = PartyAccounts.AllocationsForDocument(tx, cfg, id).Select(a => new Dictionary<string, object?>
        {
            ["id"] = a.Id,
            [kind.IsSale ? "receipt_id" : "payment_id"] = a.PaymentId,
            [kind.IsSale ? "receipt_number" : "payment_number"] = a.PaymentNumber,
            ["date"] = Json.D(a.Date),
            ["amount"] = Json.M(a.Amount),
            ["is_active"] = a.IsActive,
        }).ToList();
        return data;
    }

    /// <summary>The document input (DRF DocumentInputSerializer): items, discount, due date, optional payment.</summary>
    public static DocumentInput Input(JsonElement b, bool preview)
    {
        var items = b.List("items").Select((i, n) =>
        {
            var discountType = DiscountTypes.Parse(i.Str("discount_type"));
            var quantity = i.ReqDec("quantity");
            if (quantity < 0.001m)
                throw new ValidationError("items", "Ensure this value is greater than or equal to 0.001.");
            return new LineItemInput(i.ReqLong("product"), quantity, i.Dec("unit_price") ?? i.Dec("unit_cost"), discountType, i.Dec("discount_value") ?? 0);
        }).ToList();
        if (items.Count == 0)
            throw new ValidationError("items", "Add at least one item.");
        PaymentInput? payment = null;
        if (b.Has("payment") && b.GetProperty("payment").ValueKind == JsonValueKind.Object)
        {
            var p = b.GetProperty("payment");
            payment = new PaymentInput
            {
                PayInFull = p.Bool("pay_in_full") ?? false, Amount = p.Dec("amount"), PaymentMethod = p.Str("payment_method"),
                AccountId = p.Long("account"), ReferenceNumber = p.Str("reference_number") ?? "",
            };
            if (!payment.PayInFull && (payment.Amount ?? 0) <= 0)
                throw new ValidationError("amount", "Enter the amount paid.");
        }
        return new DocumentInput
        {
            PartyId = preview ? b.Long("party") : b.ReqLong("party"),
            Date = b.ReqDate("date"),
            DueDate = b.Date("due_date"),
            DiscountType = DiscountTypes.Parse(b.Str("discount_type")),
            DiscountValue = b.Dec("discount_value") ?? 0,
            Notes = b.Str("notes") ?? "",
            VendorBillNumber = b.Str("vendor_bill_number") ?? "",
            Items = items,
            Payment = payment,
        };
    }

    public static object TotalsJson(DocumentTotals t, IReadOnlyDictionary<long, decimal>? stock) => new
    {
        subtotal = Json.M(t.Subtotal), item_discount_total = Json.M(t.ItemDiscountTotal), net_subtotal = Json.M(t.NetSubtotal),
        discount_type = t.DiscountType.ToCode(), discount_value = Json.M(t.DiscountValue), discount_amount = Json.M(t.DiscountAmount),
        total_discount = Json.M(t.TotalDiscount), taxable_amount = Json.M(t.TaxableAmount), tax_amount = Json.M(t.TaxAmount),
        total_amount = Json.M(t.TotalAmount),
        lines = t.Lines.Select(l =>
        {
            var p = (Product)l.Ref!;
            var line = new Dictionary<string, object?>
            {
                ["quantity"] = Json.Q(l.Quantity), ["unit_price"] = Json.M(l.UnitPrice), ["gross_amount"] = Json.M(l.GrossAmount),
                ["discount_type"] = l.DiscountType.ToCode(), ["discount_value"] = Json.M(l.DiscountValue), ["discount_amount"] = Json.M(l.DiscountAmount),
                ["net_amount"] = Json.M(l.NetAmount), ["invoice_discount_share"] = Json.M(l.InvoiceDiscountShare), ["taxable_amount"] = Json.M(l.TaxableAmount),
                ["tax_rate"] = Json.R(l.TaxRate), ["tax_amount"] = Json.M(l.TaxAmount), ["total"] = Json.M(l.Total),
                ["product"] = p.Id, ["product_name"] = p.Name, ["sku_code"] = p.SkuCode, ["unit"] = p.Unit.Display(),
            };
            if (stock is not null)
                line["available_stock"] = stock.TryGetValue(p.Id, out var s) ? Json.Q(s) : null;
            return line;
        }).ToList(),
    };

    public static TradeFilter Filter(Api a, TradeKind kind) => new()
    {
        Start = a.Date("start_date"), End = a.Date("end_date"), PartyId = a.Int(PartyKey(kind)) ?? a.Int("party"),
        Number = a.Q(Number(kind)) ?? a.Q("number"), PaymentStatus = a.Q("payment_status"), Status = a.Q("status"),
        Search = a.Q("search"), Ordering = a.Q("ordering"),
    };

    public static void Map(RouteGroupBuilder api)
    {
        foreach (var kind in new[] { TradeKind.Sale, TradeKind.Purchase })
        {
            var route = kind.Table;
            var perm = kind.Permission;

            api.MapGet(route, (HttpContext h) =>
            {
                var a = Api.Of(h);
                a.Require(perm + ".view");
                var filter = Filter(a, kind);
                // The list shows every status unless one is asked for (the report defaults to active only).
                var page = a.Read(tx => BusinessReports.TradeRows(tx, kind, filter with { Status = filter.Status ?? "ALL" }, a.Page));
                return Api.Paged(page, r => ListJson(kind, r.Document, r.Items, r.Quantity));
            });

            api.MapGet($"{route}/{{id:long}}", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                a.Require(perm + ".view");
                return Api.Ok(a.Read(tx => DetailJson(tx, kind, id)));
            });

            api.MapPost(route, async (HttpContext h) =>
            {
                var a = Api.Of(h);
                var body = await a.BodyAsync();
                var input = Input(body, preview: false);
                // A sale made on a phone keeps the number the phone gave it (only from the online-copy sync).
                if (h.Items.ContainsKey("internal") && body.Str("number") is { Length: > 0 } phoneNumber)
                    input = input with { Number = phoneNumber };
                var result = a.Write((tx, actor) => TradeService.Create(tx, actor, kind, input));
                var data = a.Read(tx => DetailJson(tx, kind, result.Document.Id));
                if (kind.IsSale)
                    data["warnings"] = result.Warnings;
                return Api.Created(data, kind.IsSale ? $"Sale {result.Document.Number} created successfully." : $"Purchase {result.Document.Number} recorded successfully.");
            });

            api.MapPost($"{route}/calculate", async (HttpContext h) =>
            {
                var a = Api.Of(h);
                var input = Input(await a.BodyAsync(), preview: true);
                var actor = a.Actor;
                var preview = a.Read(tx => TradeService.Calculate(tx, actor, kind, input));
                return Api.Ok(TotalsJson(preview.Totals, preview.AvailableStock));
            });

            api.MapPost($"{route}/{{id:long}}/cancel", async (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var b = await a.BodyAsync();
                var reason = b.ReqStr("reason");
                var doc = a.Write((tx, actor) => TradeService.Cancel(tx, actor, kind, id, reason));
                return Api.Ok(a.Read(tx => DetailJson(tx, kind, id)), $"{kind.Label} {doc.Number} cancelled.");
            });

            api.MapGet($"{route}/{{id:long}}/pdf", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                a.Require(perm + ".view");
                var (pdf, number) = a.Write((tx, actor) => (DocumentPdf.TradeDocument(tx, kind, id, actor, preview: a.Q("preview") == "1"), TradeService.Get(tx, kind, id).Number));
                return Api.File(pdf, "application/pdf", $"{number}.pdf", inline: a.Q("inline") == "1");
            });
        }
    }
}
