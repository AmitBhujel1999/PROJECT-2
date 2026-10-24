using Accounting.Core;
using Accounting.Core.Common;
using Accounting.Core.Inventory;
using Accounting.Core.Products;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>/api/inventory/: stock register, product stock, stock ledger and adjustments.</summary>
public static class InventoryApi
{
    public static object RegisterJson(StockRegisterRow r, decimal openingStock) => new
    {
        id = r.ProductId, sku_code = r.SkuCode, name = r.Name, unit = r.Unit.ToUpperInvariant(), unit_display = r.Unit, opening_stock = Json.Q(openingStock),
        total_purchased = Json.Q(r.TotalPurchased), total_sold = Json.Q(r.TotalSold), total_adjusted = Json.Q(r.TotalAdjusted),
        current_stock = Json.Q(r.CurrentStock), purchase_price = Json.M(r.PurchasePrice), selling_price = Json.M(r.SellingPrice),
        cost_value = Json.M(r.CostValue), retail_value = Json.M(r.RetailValue), reorder_level = Json.Q(r.ReorderLevel),
        stock_status = r.StockStatus, is_active = r.IsActive,
    };

    public static object LedgerJson(StockLedgerRow r) => new
    {
        id = r.Id, date = Json.D(r.Date), product_id = r.ProductId, product_name = r.ProductName, sku_code = r.SkuCode, unit = r.Unit,
        transaction_type = r.TransactionType, transaction_type_display = r.TransactionTypeDisplay, reference_type = r.ReferenceType,
        reference_id = r.ReferenceId, reference_number = r.ReferenceNumber, opening_quantity = Json.Q(r.OpeningQuantity),
        quantity_in = Json.Q(r.QuantityIn), quantity_out = Json.Q(r.QuantityOut), closing_quantity = Json.Q(r.ClosingQuantity),
        running_balance = Json.Q(r.RunningBalance), notes = r.Notes, created_at = Json.T(r.CreatedAt), created_by = r.CreatedBy,
    };

    public static StockQueries.RegisterFilter RegisterFilter(Api a) => new()
    {
        Search = a.Q("search"), IsActive = a.Bool("is_active"), Status = a.Q("status"), AsOf = a.Date("as_of") ?? a.Date("end_date"),
        Start = a.Date("start_date"), ProductId = a.Int("product"), Ordering = a.Q("ordering"),
    };

    public static StockQueries.LedgerFilter LedgerFilter(Api a) => new()
    {
        ProductId = a.Int("product"), TransactionType = a.Q("transaction_type"), Start = a.Date("start_date"), End = a.Date("end_date"), Search = a.Q("search"),
    };

    private static Dictionary<long, decimal> Openings(Core.Data.Tx tx) =>
        tx.Rows("SELECT id, opening_stock FROM products").ToDictionary(r => r.Long("id"), r => r.Qty("opening_stock"));

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("inventory", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("inventory.view");
            if (a.Q("export") is { } kind)
                return ReportsApi.Export(a, kind, ReportsApi.StockTable(a));
            var filter = RegisterFilter(a);
            var (page, summary, openings) = a.Read(tx => (StockQueries.Register(tx, filter, a.Page), StockQueries.Summary(tx, filter), Openings(tx)));
            return Api.Paged(page, r => RegisterJson(r, openings.GetValueOrDefault(r.ProductId)), new Dictionary<string, object?>
            {
                ["summary"] = new
                {
                    total_products = summary.ProductCount, total_cost_value = Json.M(summary.TotalCostValue), total_retail_value = Json.M(summary.TotalRetailValue),
                    low_stock_count = summary.LowStockCount, out_of_stock_count = summary.OutOfStockCount, as_of = Json.D(filter.AsOf), start_date = Json.D(filter.Start),
                },
            });
        });

        api.MapGet("inventory/{productId:long}", (HttpContext h, long productId) =>
        {
            var a = Api.Of(h);
            a.Require("inventory.view");
            var asOf = a.Date("as_of");
            var start = a.Date("start_date");
            var data = a.Read(tx =>
            {
                var p = ProductService.Get(tx, productId);
                var result = new Dictionary<string, object?>
                {
                    ["product_id"] = p.Id, ["name"] = p.Name, ["sku_code"] = p.SkuCode, ["unit"] = p.Unit.Display(),
                    ["reorder_level"] = Json.Q(p.ReorderLevel), ["current_stock"] = Json.Q(InventoryService.CalculateStock(tx, p.Id)),
                };
                if (asOf is { } d)
                {
                    result["as_of"] = Json.D(d);
                    result["stock_as_of"] = Json.Q(InventoryService.CalculateHistoricalStock(tx, p.Id, d));
                }
                if (start is { } s)
                {
                    var end = a.Date("end_date") ?? tx.Today;
                    var period = InventoryService.CalculateStockBetween(tx, p.Id, s, end);
                    result["period"] = new
                    {
                        start_date = Json.D(s), end_date = Json.D(end), opening = Json.Q(period.Opening), quantity_in = Json.Q(period.QuantityIn),
                        quantity_out = Json.Q(period.QuantityOut), closing = Json.Q(period.Closing),
                    };
                }
                return result;
            });
            return Api.Ok(data);
        });

        api.MapGet("inventory/ledger", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("inventory.view");
            if (a.Q("export") is { } kind)
                return ReportsApi.Export(a, kind, ReportsApi.StockLedgerTable(a));
            return Api.Paged(a.Read(tx => StockQueries.Ledger(tx, LedgerFilter(a), a.Page)), LedgerJson);
        });

        // ------------------------------------------------------------------ adjustments
        const string adjustmentSelect =
            """
            SELECT x.*, p.name AS product_name, p.sku_code, u.username AS created_by_name FROM stock_adjustments x
            JOIN products p ON p.id = x.product_id LEFT JOIN users u ON u.id = x.created_by
            """;
        static object AdjustmentJson(Core.Data.Row r) => new
        {
            id = r.Long("id"), adjustment_number = r.Str("adjustment_number"), product = r.Long("product_id"), product_name = r.Str("product_name"),
            sku_code = r.Str("sku_code"), date = Json.D(r.Date("date")), quantity = Json.Q(r.Qty("quantity")), reason = r.Str("reason"),
            reason_display = AdjustmentReason.Display(r.Str("reason")), notes = r.Str("notes"), stock_before = Json.Q(r.Qty("stock_before")),
            stock_after = Json.Q(r.Qty("stock_after")), created_at = Json.T(r.Timestamp("created_at")), created_by_name = r.NullableStr("created_by_name"),
        };

        api.MapGet("inventory/adjustments", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("inventory.view");
            var args = new
            {
                product = a.Int("product"), reason = a.Q("reason"), start = a.Date("start_date"), end = a.Date("end_date"),
                search = ProductService.LikePattern(a.Q("search")), limit = a.Page.Size, offset = a.Page.Offset,
            };
            const string where =
                """
                WHERE (@product IS NULL OR x.product_id = @product) AND (@reason IS NULL OR x.reason = @reason)
                  AND (@start IS NULL OR x.date >= @start) AND (@end IS NULL OR x.date <= @end)
                  AND (@search IS NULL OR x.adjustment_number LIKE @search OR p.name LIKE @search OR p.sku_code LIKE @search OR x.notes LIKE @search)
                """;
            var page = a.Read(tx =>
            {
                var count = tx.Scalar<long>($"SELECT COUNT(*) FROM stock_adjustments x JOIN products p ON p.id = x.product_id {where}", args);
                var rows = tx.Rows($"{adjustmentSelect} {where} ORDER BY x.date DESC, x.id DESC LIMIT @limit OFFSET @offset", args).Select(AdjustmentJson).ToList();
                return new Page<object>(rows, (int)count, a.Page.Page, a.Page.Size);
            });
            return Api.Paged(page, x => x);
        });

        api.MapGet("inventory/adjustments/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("inventory.view");
            return Api.Ok(a.Read(tx => tx.Rows(adjustmentSelect + " WHERE x.id = @id", new { id }).Select(AdjustmentJson).FirstOrDefault())
                          ?? throw new NotFoundError("The requested record was not found."));
        });

        api.MapPost("inventory/adjustments", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var productId = b.ReqLong("product");
            var quantity = b.ReqDec("quantity");
            if (quantity == 0)
                throw new ValidationError("quantity", "Quantity cannot be zero.");
            var reason = b.ReqStr("reason");
            var product = a.Read(tx => ProductService.Find(tx, productId));
            if (product is not { IsActive: true })
                throw new ValidationError("product", $"Invalid pk \"{productId}\" - object does not exist.");
            var adjustment = a.Write((tx, actor) => InventoryService.CreateAdjustment(tx, actor, productId, quantity, reason, b.ReqDate("date"), b.Str("notes") ?? ""));
            var json = a.Read(tx => tx.Rows(adjustmentSelect + " WHERE x.id = @id", new { id = adjustment.Id }).Select(AdjustmentJson).First());
            return Api.Created(json, $"Stock adjustment {adjustment.AdjustmentNumber} recorded for {product.Name}.");
        });
    }
}
