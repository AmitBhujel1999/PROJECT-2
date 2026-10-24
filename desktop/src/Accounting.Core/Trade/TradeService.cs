using Accounting.Core.Audit;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Inventory;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Products;
using Accounting.Core.Security;

namespace Accounting.Core.Trade;

/// <summary>
/// Sales and purchases engine, ported from backend/apps/sales/services.py and purchases/services.py.
///
/// Sale:     validate stock -> invoice + lines -> stock ledger OUT -> optional payment.
/// Purchase: bill + lines -> stock ledger IN -> optional payment.
/// Everything runs in the caller's write transaction, so any failure (not enough stock,
/// bad discount, ...) saves nothing and does not use up a document number.
/// </summary>
public static class TradeService
{
    // ------------------------------------------------------------------ input

    private static void Validate(DocumentInput input, bool preview)
    {
        if (!preview && input.PartyId is null)
            throw new ValidationError("party", "This field is required.");
        if (input.Items.Count == 0)
            throw new ValidationError("items", "Add at least one item.");
        if (input.Items.Count > DocumentInput.MaxLines)
            throw new ValidationError("items", $"A document can contain at most {DocumentInput.MaxLines} lines.");
        if (input.DueDate is { } due && due < input.Date)
            throw new ValidationError("due_date", "Due date cannot be before the document date.");
        if (input.DiscountValue < 0)
            throw new ValidationError("discount_value", "Ensure this value is greater than or equal to 0.");
        if (input.Notes.Length > 2000)
            throw new ValidationError("notes", "Ensure this field has no more than 2000 characters.");
        if (input.VendorBillNumber.Length > 60)
            throw new ValidationError("vendor_bill_number", "Ensure this field has no more than 60 characters.");
        foreach (var item in input.Items)
        {
            if (Money.Qty(item.Quantity) < 0.001m)
                throw new ValidationError("items", "Quantity must be at least 0.001.");
            if (item.UnitPrice < 0)
                throw new ValidationError("items", "Price cannot be negative.");
            if (item.DiscountValue < 0)
                throw new ValidationError("items", "Discount cannot be negative.");
        }
        if (input.Payment is { PayInFull: false } p && (p.Amount ?? 0) <= 0)
            throw new ValidationError("amount", "Enter the amount paid.");
    }

    private static Party ActiveParty(Tx tx, TradeKind kind, long partyId)
    {
        var party = tx.Rows("SELECT * FROM parties WHERE id = @partyId AND type = @type", new { partyId, type = kind.PartyType.ToCode() })
            .Select(PartyService.Map).FirstOrDefault()
            ?? throw new BusinessError($"{kind.PartyLabel} not found.", kind.IsSale ? "CUSTOMER_NOT_FOUND" : "VENDOR_NOT_FOUND");
        if (!party.IsActive)
            throw new BusinessError($"{kind.PartyLabel} {party.Name} is inactive.", "PARTY_INACTIVE");
        return party;
    }

    private static Dictionary<long, Product> ActiveProducts(Tx tx, TradeKind kind, IEnumerable<LineItemInput> items)
    {
        var ids = items.Select(i => i.ProductId).Distinct().Order().ToList();
        var products = ids.Select(id => ProductService.Find(tx, id)).OfType<Product>().ToDictionary(p => p.Id);
        var missing = ids.Where(id => !products.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new BusinessError($"Product(s) not found: [{string.Join(", ", missing)}]", "PRODUCT_NOT_FOUND", 404);
        var inactive = products.Values.Where(p => !p.IsActive).Select(p => p.Name).ToList();
        if (inactive.Count > 0)
            throw new BusinessError($"Inactive product(s) cannot be {(kind.IsSale ? "sold" : "used")}: {string.Join(", ", inactive)}", "PRODUCT_INACTIVE");
        return products;
    }

    /// <summary>The unit price defaults to the product's price; the tax rate always comes from the product master.</summary>
    private static DocumentTotals BuildTotals(TradeKind kind, DocumentInput input, Dictionary<long, Product> products) =>
        Calculations.CalculateDocument(
            input.Items.Select(i => new LineInput(i.Quantity, i.UnitPrice ?? kind.DefaultPrice(products[i.ProductId]),
                products[i.ProductId].TaxRate, i.DiscountType, i.DiscountValue, products[i.ProductId])).ToList(),
            input.DiscountType, input.DiscountValue);

    private static DateOnly ResolveDueDate(Actor actor, DateOnly date, DateOnly? requested, int creditDays)
    {
        var standard = date.AddDays(creditDays);
        if (requested is null || requested == standard)
            return standard;
        if (!Permissions.Has(actor, "documents.override_due_date"))
            throw new BusinessError("You are not allowed to override the due date calculated from the credit terms.",
                "DUE_DATE_OVERRIDE_DENIED", 403);
        return requested.Value;
    }

    private static Dictionary<long, decimal> Requirements(DocumentInput input) =>
        input.Items.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.Sum(i => Money.Qty(i.Quantity)));

    // ------------------------------------------------------------------ commands

    /// <summary>Totals for a document being entered (party may be missing). Sales also return available stock.</summary>
    public static TradePreview Calculate(Tx tx, Actor actor, TradeKind kind, DocumentInput input)
    {
        Permissions.Require(actor, kind.Permission + ".create");
        Validate(input with { Payment = null }, preview: true);
        var products = ActiveProducts(tx, kind, input.Items);
        var totals = BuildTotals(kind, input, products);
        var stock = kind.IsSale
            ? products.Keys.ToDictionary(id => id, id => Math.Min(InventoryService.CalculateStock(tx, id), InventoryService.AvailableOn(tx, id, input.Date)))
            : null;
        return new TradePreview(totals, products.Values.ToList(), stock);
    }

    /// <summary>Save an invoice or bill. Warnings list products that sank to or below their reorder level (sales).</summary>
    public static SaveResult Create(Tx tx, Actor actor, TradeKind kind, DocumentInput input)
    {
        Permissions.Require(actor, kind.Permission + ".create");
        tx.RequireWrite();
        Validate(input, preview: false);
        var party = ActiveParty(tx, kind, input.PartyId!.Value);
        var products = ActiveProducts(tx, kind, input.Items);
        if (kind.IsSale)
            InventoryService.CheckAvailability(tx, Requirements(input), input.Date);
        var totals = BuildTotals(kind, input, products);
        var dueDate = ResolveDueDate(actor, input.Date, input.DueDate, party.CreditDays);
        var number = Sequences.Next(tx, kind.SequenceKey);
        var id = tx.Insert(
            $"""
            INSERT INTO {kind.Table} (number, party_id, {(kind.IsSale ? "" : "vendor_bill_number, ")}date, due_date, subtotal, item_discount_total,
                discount_type, discount_value, discount_amount, taxable_amount, tax_amount, total_amount, notes, created_by)
            VALUES (@number, @partyId, {(kind.IsSale ? "" : "@vendorBillNumber, ")}@date, @dueDate, @subtotal, @itemDiscounts,
                @discountType, @discountValue, @discountAmount, @taxable, @tax, @total, @notes, @UserId)
            """, new
            {
                number, partyId = party.Id, vendorBillNumber = input.VendorBillNumber.Trim(), date = input.Date, dueDate,
                subtotal = Money.ToPaisa(totals.Subtotal), itemDiscounts = Money.ToPaisa(totals.ItemDiscountTotal),
                discountType = totals.DiscountType.ToCode(), discountValue = Money.ToPaisa(totals.DiscountValue),
                discountAmount = Money.ToPaisa(totals.DiscountAmount), taxable = Money.ToPaisa(totals.TaxableAmount),
                tax = Money.ToPaisa(totals.TaxAmount), total = Money.ToPaisa(totals.TotalAmount), notes = input.Notes.Trim(), actor.UserId,
            });
        foreach (var line in totals.Lines)
        {
            var product = (Product)line.Ref!;
            tx.Execute(
                $"""
                INSERT INTO {kind.ItemsTable} (document_id, product_id, quantity, unit_price, gross_amount, discount_type, discount_value,
                    discount_amount, invoice_discount_share, taxable_amount, tax_rate, tax_amount, line_total)
                VALUES (@id, @productId, @quantity, @unitPrice, @gross, @discountType, @discountValue, @discountAmount, @share,
                    @taxable, @taxRate, @tax, @total)
                """, new
                {
                    id, productId = product.Id, quantity = Money.ToMilli(line.Quantity), unitPrice = Money.ToPaisa(line.UnitPrice),
                    gross = Money.ToPaisa(line.GrossAmount), discountType = line.DiscountType.ToCode(),
                    discountValue = Money.ToPaisa(line.DiscountValue), discountAmount = Money.ToPaisa(line.DiscountAmount),
                    share = Money.ToPaisa(line.InvoiceDiscountShare), taxable = Money.ToPaisa(line.TaxableAmount),
                    taxRate = Money.ToPaisa(line.TaxRate), tax = Money.ToPaisa(line.TaxAmount), total = Money.ToPaisa(line.Total),
                });
            // Purchases carry the net landed cost per unit (after all discounts, excluding recoverable VAT).
            InventoryService.PostMovement(tx, actor, product, input.Date, kind.Movement,
                quantityIn: kind.IsSale ? 0 : line.Quantity, quantityOut: kind.IsSale ? line.Quantity : 0,
                referenceType: kind.ReferenceType, referenceId: id, referenceNumber: number,
                unitCost: kind.IsSale ? product.PurchasePrice : Money.Round(line.TaxableAmount / line.Quantity),
                notes: kind.IsSale ? $"Sale to {party.Name}" : $"Purchase from {party.Name}");
        }
        if (input.Payment is { } payment)
            PartyAccounts.PayOnCreation(tx, actor, AccountConfig.For(kind), Get(tx, kind, id), payment);
        var doc = Get(tx, kind, id);
        AuditLog.Record(tx, actor, AuditAction.Create, kind.ModelName, id, number, after: AuditLog.Snapshot(doc));
        return new SaveResult(doc, kind.IsSale ? StockWarnings(tx, products.Values) : []);
    }

    private static List<string> StockWarnings(Tx tx, IEnumerable<Product> products)
    {
        var warnings = new List<string>();
        foreach (var product in products)
        {
            var stock = InventoryService.CalculateStock(tx, product.Id);
            if (stock <= product.ReorderLevel)
                warnings.Add(stock > 0
                    ? $"{product.Name} is below reorder level ({stock.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {product.Unit.Display()} left)."
                    : $"{product.Name} is now out of stock.");
        }
        return warnings;
    }

    /// <summary>
    /// Cancel an invoice or bill: stock is reversed with new ledger rows dated today and any
    /// payments against it become unallocated advances. Nothing is deleted.
    /// </summary>
    public static TradeDocument Cancel(Tx tx, Actor actor, TradeKind kind, long id, string reason)
    {
        Permissions.Require(actor, kind.Permission + ".cancel");
        tx.RequireWrite();
        var before = Get(tx, kind, id);
        if (before.IsCancelled)
            throw new BusinessError($"{before.Number} is already cancelled.", "ALREADY_CANCELLED");
        if (string.IsNullOrWhiteSpace(reason))
            throw new BusinessError("A cancellation reason is required.", "REASON_REQUIRED");
        foreach (var line in before.Lines)
        {
            var product = ProductService.Get(tx, line.ProductId);
            try
            {
                InventoryService.PostMovement(tx, actor, product, tx.Today, kind.CancelMovement,
                    quantityIn: kind.IsSale ? line.Quantity : 0, quantityOut: kind.IsSale ? 0 : line.Quantity,
                    referenceType: kind.ReferenceType, referenceId: id, referenceNumber: before.Number,
                    unitCost: kind.IsSale ? product.PurchasePrice : line.UnitPrice, notes: $"Cancellation of {before.Number}");
            }
            catch (InsufficientStockError err) when (!kind.IsSale)
            {
                throw new InsufficientStockError(
                    $"Cannot cancel {before.Number}: stock of {product.Name} has already been used. {err.Message}", err.Details);
            }
        }
        PartyAccounts.ReleaseDocumentAllocations(tx, actor, AccountConfig.For(kind), before);
        tx.Execute(
            $"""
            UPDATE {kind.Table} SET status = 'CANCELLED', cancelled_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), cancelled_on = @today,
                cancelled_by = @UserId, cancel_reason = @reason, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @id
            """, new { id, today = tx.Today, actor.UserId, reason = reason.Trim()[..Math.Min(reason.Trim().Length, 255)] });
        var after = Get(tx, kind, id);
        AuditLog.Record(tx, actor, AuditAction.Cancel, kind.ModelName, id, after.Number, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    // ------------------------------------------------------------------ queries

    private static string Select(TradeKind kind) =>
        $"""
        SELECT d.*, p.name AS party_name, u.username AS created_by_name {(kind.IsSale ? ", '' AS vendor_bill_number" : "")}
        FROM {kind.Table} d
        JOIN parties p ON p.id = d.party_id
        JOIN users u ON u.id = d.created_by
        """;

    public static TradeDocument Get(Tx tx, TradeKind kind, long id) =>
        Find(tx, kind, id) ?? throw new NotFoundError($"{kind.Label} not found.");

    public static TradeDocument? Find(Tx tx, TradeKind kind, long id)
    {
        var doc = tx.Rows(Select(kind) + " WHERE d.id = @id", new { id }).Select(r => Map(kind, r)).FirstOrDefault();
        return doc is null ? null : doc with { Lines = Lines(tx, kind, id) };
    }

    private static IReadOnlyList<TradeLine> Lines(Tx tx, TradeKind kind, long documentId) =>
        tx.Rows($"""
                 SELECT i.*, p.name AS product_name, p.sku_code, p.unit FROM {kind.ItemsTable} i
                 JOIN products p ON p.id = i.product_id WHERE i.document_id = @documentId ORDER BY i.id
                 """, new { documentId })
            .Select(r => new TradeLine
            {
                Id = r.Long("id"),
                ProductId = r.Long("product_id"),
                ProductName = r.Str("product_name"),
                SkuCode = r.Str("sku_code"),
                Unit = Units.Parse(r.Str("unit")).Display(),
                Quantity = r.Qty("quantity"),
                UnitPrice = r.Money("unit_price"),
                GrossAmount = r.Money("gross_amount"),
                DiscountType = DiscountTypes.Parse(r.NullableStr("discount_type")),
                DiscountValue = r.Money("discount_value"),
                DiscountAmount = r.Money("discount_amount"),
                InvoiceDiscountShare = r.Money("invoice_discount_share"),
                TaxableAmount = r.Money("taxable_amount"),
                TaxRate = r.Rate("tax_rate"),
                TaxAmount = r.Money("tax_amount"),
                LineTotal = r.Money("line_total"),
            }).ToList();

    /// <summary>Documents newest first, without their lines.</summary>
    public static Page<TradeDocument> List(Tx tx, TradeKind kind, TradeFilter filter, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        var where =
            $"""
            WHERE (@start IS NULL OR d.date >= @start) AND (@end IS NULL OR d.date <= @end)
              AND (@partyId IS NULL OR d.party_id = @partyId) AND (@number IS NULL OR d.number LIKE @number)
              AND (@paymentStatus IS NULL OR d.payment_status = @paymentStatus) AND (@status IS NULL OR d.status = @status)
              AND (@search IS NULL OR d.number LIKE @search OR p.name LIKE @search OR p.pan_vat_no LIKE @search
                   OR {(kind.IsSale ? "p.phone" : "d.vendor_bill_number")} LIKE @search)
            """;
        var args = new
        {
            start = filter.Start, end = filter.End, partyId = filter.PartyId, number = ProductService.LikePattern(filter.Number),
            paymentStatus = filter.PaymentStatus, status = filter.Status, search = ProductService.LikePattern(filter.Search),
            limit = page.Size, offset = page.Offset,
        };
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM {kind.Table} d JOIN parties p ON p.id = d.party_id {where}", args);
        var rows = tx.Rows($"{Select(kind)} {where} ORDER BY {OrderBy(filter.Ordering)} LIMIT @limit OFFSET @offset", args)
            .Select(r => Map(kind, r)).ToList();
        return new Page<TradeDocument>(rows, (int)count, page.Page, page.Size);
    }

    private static readonly Dictionary<string, string> Orderings = new()
    {
        ["date"] = "d.date", ["number"] = "d.number", ["invoice_number"] = "d.number", ["bill_number"] = "d.number",
        ["total_amount"] = "d.total_amount", ["due_date"] = "d.due_date",
    };

    private static string OrderBy(string? ordering) =>
        ordering is { } o && Orderings.TryGetValue(o.TrimStart('-'), out var column)
            ? $"{column}{(o.StartsWith('-') ? " DESC" : "")}, d.id{(o.StartsWith('-') ? " DESC" : "")}"
            : "d.date DESC, d.id DESC";

    private static TradeDocument Map(TradeKind kind, Row r) => new()
    {
        Id = r.Long("id"),
        Kind = kind,
        Number = r.Str("number"),
        PartyId = r.Long("party_id"),
        PartyName = r.Str("party_name"),
        VendorBillNumber = r.Str("vendor_bill_number"),
        Date = r.Date("date"),
        DueDate = r.Date("due_date"),
        Subtotal = r.Money("subtotal"),
        ItemDiscountTotal = r.Money("item_discount_total"),
        DiscountType = DiscountTypes.Parse(r.NullableStr("discount_type")),
        DiscountValue = r.Money("discount_value"),
        DiscountAmount = r.Money("discount_amount"),
        TaxableAmount = r.Money("taxable_amount"),
        TaxAmount = r.Money("tax_amount"),
        TotalAmount = r.Money("total_amount"),
        AmountPaid = r.Money("amount_paid"),
        PaymentStatus = r.Str("payment_status"),
        Status = r.Str("status"),
        Notes = r.Str("notes"),
        CancelledAt = r.NullableStr("cancelled_at") is null ? null : r.Timestamp("cancelled_at"),
        CancelledOn = r.NullableDate("cancelled_on"),
        CancelReason = r.Str("cancel_reason"),
        CreatedBy = r.Long("created_by"),
        CreatedByName = r.Str("created_by_name"),
        CancelledBy = r.NullableLong("cancelled_by"),
        CreatedAt = r.Timestamp("created_at"),
    };
}
