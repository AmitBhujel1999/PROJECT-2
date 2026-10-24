using System.Globalization;
using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Products;
using Accounting.Core.Security;

namespace Accounting.Core.Inventory;

/// <summary>
/// Inventory engine, ported from backend/apps/inventory/services.py.
///
/// All stock changes go through <see cref="PostMovement"/>, which must run inside
/// <see cref="Database.Write{T}"/>. The write lock serialises stock checks, so two
/// sales can never both consume the same units (no overselling). Quantities are
/// handled as integer thousandths here, the same as in the database.
/// </summary>
public static class InventoryService
{
    /// <summary>Current stock = balance after the most recently posted movement.</summary>
    public static decimal CalculateStock(Tx tx, long productId) => Money.FromMilli(CurrentMilli(tx, productId));

    private static long CurrentMilli(Tx tx, long productId) =>
        tx.Scalar<long?>("SELECT running_balance FROM stock_movements WHERE product_id = @productId ORDER BY id DESC LIMIT 1",
            new { productId }) ?? 0;

    /// <summary>Closing stock at date T = opening + quantity in up to T - quantity out up to T.</summary>
    public static decimal CalculateHistoricalStock(Tx tx, long productId, DateOnly asOf) => Money.FromMilli(HistoricalMilli(tx, productId, asOf));

    private static long HistoricalMilli(Tx tx, long productId, DateOnly asOf) =>
        tx.Scalar<long>("SELECT COALESCE(SUM(quantity_in - quantity_out), 0) FROM stock_movements WHERE product_id = @productId AND date <= @asOf",
            new { productId, asOf });

    public static StockPeriod CalculateStockBetween(Tx tx, long productId, DateOnly start, DateOnly end)
    {
        var opening = HistoricalMilli(tx, productId, start.AddDays(-1));
        var r = tx.Row(
            """
            SELECT COALESCE(SUM(quantity_in), 0) AS qin, COALESCE(SUM(quantity_out), 0) AS qout
            FROM stock_movements WHERE product_id = @productId AND date >= @start AND date <= @end
            """, new { productId, start, end })!;
        return new StockPeriod(Money.FromMilli(opening), r.Qty("qin"), r.Qty("qout"),
            Money.FromMilli(opening + r.Long("qin") - r.Long("qout")));
    }

    /// <summary>
    /// Quantity that can leave stock on date <paramref name="on"/> without any historical
    /// balance (on that date or any later date) becoming negative:
    /// min(closing stock at <paramref name="on"/>, closing stock after each later movement).
    /// This stops a back-dated sale from consuming units that were only received later,
    /// or that later transactions already rely on.
    /// </summary>
    public static decimal AvailableOn(Tx tx, long productId, DateOnly on) => Money.FromMilli(AvailableMilli(tx, productId, on));

    private static long AvailableMilli(Tx tx, long productId, DateOnly on)
    {
        var atDate = HistoricalMilli(tx, productId, on);
        var laterMin = tx.Scalar<long?>(
            """
            SELECT MIN(balance) FROM (
                SELECT date, SUM(quantity_in - quantity_out) OVER (ORDER BY date, id) AS balance
                FROM stock_movements WHERE product_id = @productId
            ) WHERE date > @on
            """, new { productId, on });
        var available = laterMin is { } m ? Math.Min(atDate, m) : atDate;
        return Math.Max(available, 0);
    }

    private static InsufficientStockError Insufficient(Product product, long available, long requested, DateOnly on)
    {
        var unit = product.Unit.Display();
        return new InsufficientStockError(
            $"Insufficient stock for {product.Name}. Available: {Plain(available)} {unit}, requested: {Plain(requested)} {unit}.",
            new Dictionary<string, object?>
            {
                ["product_id"] = product.Id,
                ["product"] = product.Name,
                ["sku_code"] = product.SkuCode,
                ["available"] = Fixed3(available),
                ["requested"] = Fixed3(requested),
                ["unit"] = unit,
                ["date"] = on.ToString("yyyy-MM-dd"),
            });
    }

    /// <summary>Quantity without trailing zeros ("2.5", "10"), for messages.</summary>
    internal static string Plain(long milli) => Money.FromMilli(milli).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Quantity with 3 decimals ("2.500"), the API's format.</summary>
    internal static string Fixed3(long milli) => Money.FromMilli(milli).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Append one ledger row. Outward quantity is checked against stock available on the date.</summary>
    public static StockMovement PostMovement(
        Tx tx, Actor actor, Product product, DateOnly date, string transactionType,
        decimal quantityIn = 0, decimal quantityOut = 0, string referenceType = "", long? referenceId = null,
        string referenceNumber = "", decimal unitCost = 0, string notes = "")
    {
        tx.RequireWrite();
        long qin = Money.ToMilli(quantityIn), qout = Money.ToMilli(quantityOut);
        if (qin > 0 == qout > 0 || qin < 0 || qout < 0)
            throw new BusinessError("A stock movement must be either inward or outward.", "INVALID_MOVEMENT");
        var current = CurrentMilli(tx, product.Id);
        if (qout > 0)
        {
            var available = Math.Min(current, AvailableMilli(tx, product.Id, date));
            if (qout > available)
                throw Insufficient(product, available, qout, date);
        }
        var id = tx.Insert(
            """
            INSERT INTO stock_movements (product_id, date, transaction_type, reference_type, reference_id, reference_number,
                                         quantity_in, quantity_out, running_balance, unit_cost, notes, created_by)
            VALUES (@productId, @date, @transactionType, @referenceType, @referenceId, @referenceNumber,
                    @qin, @qout, @balance, @unitCost, @notes, @UserId)
            """, new
            {
                productId = product.Id, date, transactionType, referenceType, referenceId, referenceNumber, qin, qout,
                balance = current + qin - qout, unitCost = Money.ToPaisa(unitCost),
                notes = notes.Length > 255 ? notes[..255] : notes, actor.UserId,
            });
        return GetMovement(tx, id);
    }

    public static StockMovement? PostOpeningStock(Tx tx, Actor actor, Product product, DateOnly date)
    {
        if (product.OpeningStock <= 0)
            return null;
        return PostMovement(tx, actor, product, date, MovementType.OpeningStock, quantityIn: product.OpeningStock,
            referenceType: "product", referenceId: product.Id, referenceNumber: product.SkuCode, unitCost: product.OpeningRate,
            notes: "Opening stock");
    }

    /// <summary>
    /// Change a product's opening quantity after it was created. The ledger is append-only,
    /// so the difference is posted as another opening-stock movement on the opening date.
    /// Lowering it is refused when stock would go below zero on that date or later.
    /// </summary>
    public static StockMovement? ChangeOpeningStock(Tx tx, Actor actor, Product product, decimal quantity)
    {
        long oldQty = Money.ToMilli(product.OpeningStock), newQty = Money.ToMilli(quantity);
        var delta = newQty - oldQty;
        if (delta == 0)
            return null;
        var opened = tx.Scalar<string?>(
            "SELECT date FROM stock_movements WHERE product_id = @id AND transaction_type = 'OPENING_STOCK' ORDER BY id LIMIT 1",
            new { id = product.Id });
        var first = tx.Scalar<string?>("SELECT date FROM stock_movements WHERE product_id = @id ORDER BY date, id LIMIT 1",
            new { id = product.Id });
        var created = DateOnly.FromDateTime(product.CreatedAt.ToLocalTime());
        var date = opened is not null ? DateOnly.Parse(opened, CultureInfo.InvariantCulture)
            : first is not null && DateOnly.Parse(first, CultureInfo.InvariantCulture) < created ? DateOnly.Parse(first, CultureInfo.InvariantCulture)
            : created;
        try
        {
            return PostMovement(tx, actor, product, date, MovementType.OpeningStock,
                quantityIn: Money.FromMilli(Math.Max(delta, 0)), quantityOut: Money.FromMilli(Math.Max(-delta, 0)),
                referenceType: "product", referenceId: product.Id, referenceNumber: product.SkuCode, unitCost: product.OpeningRate,
                notes: $"Opening stock changed from {Plain(oldQty)} to {Plain(newQty)}");
        }
        catch (InsufficientStockError err)
        {
            var available = Money.ToMilli(decimal.Parse((string)err.Details["available"]!, CultureInfo.InvariantCulture));
            throw new BusinessError(
                $"Opening stock of {product.Name} cannot be lowered to {Plain(newQty)}: stock already sold or adjusted needs at least {Plain(oldQty - available)}.",
                "OPENING_TOO_LOW", 400, err.Details);
        }
    }

    /// <summary>
    /// Check that each product has enough stock for the requested quantity on <paramref name="on"/>.
    /// <paramref name="requirements"/> maps product id to the total over all lines, so two lines
    /// of the same product are checked together.
    /// </summary>
    public static void CheckAvailability(Tx tx, IReadOnlyDictionary<long, decimal> requirements, DateOnly on)
    {
        foreach (var (productId, requested) in requirements.OrderBy(r => r.Key))
        {
            var product = ProductService.Get(tx, productId);
            var available = Math.Min(CurrentMilli(tx, productId), AvailableMilli(tx, productId, on));
            var wanted = Money.ToMilli(requested);
            if (wanted > available)
                throw Insufficient(product, available, wanted, on);
        }
    }

    public static StockAdjustment CreateAdjustment(Tx tx, Actor actor, long productId, decimal quantity, string reason, DateOnly date, string notes = "")
    {
        Permissions.Require(actor, "inventory.adjust");
        tx.RequireWrite();
        var qty = Money.ToMilli(quantity);
        notes = notes.Trim();
        if (qty == 0)
            throw new ValidationError("quantity", "Adjustment quantity cannot be zero.");
        if (!AdjustmentReason.All.Contains(reason))
            throw new ValidationError("reason", $"\"{reason}\" is not a valid choice.");
        if (reason == AdjustmentReason.Other && notes.Length == 0)
            throw new ValidationError("notes", "Please describe the reason for this adjustment.");
        if (notes.Length > 255)
            throw new ValidationError("notes", "Ensure this field has no more than 255 characters.");
        var product = ProductService.Get(tx, productId);
        var before = CurrentMilli(tx, productId);
        if (qty < 0)
        {
            var available = Math.Min(before, AvailableMilli(tx, productId, date));
            if (-qty > available)
                throw new InsufficientStockError(
                    $"Adjustment would make stock negative for {product.Name}. Available: {Plain(available)}.",
                    new Dictionary<string, object?> { ["product_id"] = productId, ["available"] = Fixed3(available), ["requested"] = Fixed3(-qty) });
        }
        var number = Sequences.Next(tx, Sequences.Adjustment);
        var id = tx.Insert(
            """
            INSERT INTO stock_adjustments (adjustment_number, product_id, date, quantity, reason, notes, stock_before, stock_after, created_by)
            VALUES (@number, @productId, @date, @qty, @reason, @notes, @before, @after, @UserId)
            """, new { number, productId, date, qty, reason, notes, before, after = before + qty, actor.UserId });
        PostMovement(tx, actor, product, date, MovementType.Adjustment,
            quantityIn: qty > 0 ? Money.FromMilli(qty) : 0, quantityOut: qty < 0 ? Money.FromMilli(-qty) : 0,
            referenceType: "adjustment", referenceId: id, referenceNumber: number, unitCost: product.PurchasePrice,
            notes: AdjustmentReason.Display(reason) + (notes.Length > 0 ? ": " + notes : ""));
        var adjustment = GetAdjustment(tx, id);
        AuditLog.Record(tx, actor, AuditAction.Create, "inventory.StockAdjustment", id, number, after: AuditLog.Snapshot(adjustment));
        return adjustment;
    }

    public static StockAdjustment GetAdjustment(Tx tx, long id) =>
        tx.Rows("SELECT * FROM stock_adjustments WHERE id = @id", new { id }).Select(MapAdjustment).FirstOrDefault()
        ?? throw new NotFoundError("Stock adjustment not found.");

    public static Page<StockAdjustment> ListAdjustments(Tx tx, long? productId = null, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        var args = new { productId, limit = page.Size, offset = page.Offset };
        const string where = "WHERE (@productId IS NULL OR product_id = @productId)";
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM stock_adjustments {where}", args);
        var rows = tx.Rows($"SELECT * FROM stock_adjustments {where} ORDER BY date DESC, id DESC LIMIT @limit OFFSET @offset", args)
            .Select(MapAdjustment).ToList();
        return new Page<StockAdjustment>(rows, (int)count, page.Page, page.Size);
    }

    public static StockMovement GetMovement(Tx tx, long id) =>
        tx.Rows("SELECT * FROM stock_movements WHERE id = @id", new { id }).Select(MapMovement).First();

    public static IReadOnlyList<StockMovement> Movements(Tx tx, long productId) =>
        tx.Rows("SELECT * FROM stock_movements WHERE product_id = @productId ORDER BY id", new { productId }).Select(MapMovement).ToList();

    internal static StockMovement MapMovement(Row r) => new(
        r.Long("id"), r.Long("product_id"), r.Date("date"), r.Str("transaction_type"), r.Str("reference_type"),
        r.NullableLong("reference_id"), r.Str("reference_number"), r.Qty("quantity_in"), r.Qty("quantity_out"),
        r.Qty("running_balance"), r.Money("unit_cost"), r.Str("notes"), r.Timestamp("created_at"), r.NullableLong("created_by"));

    private static StockAdjustment MapAdjustment(Row r) => new(
        r.Long("id"), r.Str("adjustment_number"), r.Long("product_id"), r.Date("date"), r.Qty("quantity"), r.Str("reason"),
        r.Str("notes"), r.Qty("stock_before"), r.Qty("stock_after"), r.Timestamp("created_at"), r.NullableLong("created_by"));
}
