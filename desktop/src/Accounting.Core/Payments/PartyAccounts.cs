using Accounting.Core.Audit;
using Accounting.Core.CashBank;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Parties;
using Accounting.Core.Products;
using Accounting.Core.Security;
using Accounting.Core.Trade;

namespace Accounting.Core.Payments;

/// <summary>
/// Generic receivable / payable engine, ported from backend/apps/common/party_accounts.py
/// (commands only; ledgers and aging come in phase 4). Nothing here stores a party balance:
/// amount_paid on documents and allocated_amount on payments are caches rebuilt from the
/// active allocations whenever they change.
/// </summary>
public static class PartyAccounts
{
    // ------------------------------------------------------------------ caches

    private static void RefreshDocument(Tx tx, AccountConfig cfg, long documentId)
    {
        var total = tx.Scalar<long>($"SELECT total_amount FROM {cfg.DocTable} WHERE id = @documentId", new { documentId });
        var paid = tx.Scalar<long>($"SELECT COALESCE(SUM(amount), 0) FROM {cfg.AllocationTable} WHERE document_id = @documentId AND is_active = 1",
            new { documentId });
        tx.Execute($"UPDATE {cfg.DocTable} SET amount_paid = @paid, payment_status = @status, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = @documentId",
            new { documentId, paid, status = PaymentStatus.For(Money.FromPaisa(total), Money.FromPaisa(paid)) });
    }

    private static void RefreshPayment(Tx tx, AccountConfig cfg, long paymentId) =>
        tx.Execute(
            $"""
            UPDATE {cfg.PaymentTable} SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                allocated_amount = (SELECT COALESCE(SUM(amount), 0) FROM {cfg.AllocationTable} WHERE payment_id = @paymentId AND is_active = 1)
            WHERE id = @paymentId
            """, new { paymentId });

    // ------------------------------------------------------------------ commands

    private static Party ActiveParty(Tx tx, AccountConfig cfg, long partyId)
    {
        var party = tx.Rows("SELECT * FROM parties WHERE id = @partyId AND type = @type", new { partyId, type = cfg.PartyType.ToCode() })
            .Select(PartyService.Map).FirstOrDefault()
            ?? throw new BusinessError($"{cfg.Documents.PartyLabel} not found.", "PARTY_NOT_FOUND");
        if (!party.IsActive)
            throw new BusinessError($"{party.Name} is inactive.", "PARTY_INACTIVE");
        return party;
    }

    /// <summary>Record a receipt/payment (needs receipts.create / payments.create).</summary>
    public static PartyPayment CreatePayment(
        Tx tx, Actor actor, AccountConfig cfg, long partyId, DateOnly date, decimal amount, string? paymentMethod = null,
        long? accountId = null, string referenceNumber = "", string notes = "", IReadOnlyList<AllocationRequest>? allocations = null,
        bool autoAllocate = false)
    {
        Permissions.Require(actor, cfg.Permission + ".create");
        return Insert(tx, actor, cfg, partyId, date, amount, paymentMethod, accountId, referenceNumber, notes, allocations, autoAllocate);
    }

    private static PartyPayment Insert(
        Tx tx, Actor actor, AccountConfig cfg, long partyId, DateOnly date, decimal amount, string? paymentMethod,
        long? accountId, string referenceNumber, string notes, IReadOnlyList<AllocationRequest>? allocations, bool autoAllocate)
    {
        tx.RequireWrite();
        ActiveParty(tx, cfg, partyId);
        var (account, method) = MoneyAccounts.Resolve(tx, accountId, paymentMethod);
        amount = Money.Round(amount);
        if (amount <= 0)
            throw new BusinessError("Amount must be greater than zero.", "INVALID_AMOUNT");
        if (referenceNumber.Length > 60)
            throw new ValidationError("reference_number", "Ensure this field has no more than 60 characters.");
        var id = tx.Insert(
            $"""
            INSERT INTO {cfg.PaymentTable} (number, party_id, date, amount, payment_method, account_id, reference_number, notes, created_by)
            VALUES (@number, @partyId, @date, @amount, @method, @accountId, @referenceNumber, @notes, @UserId)
            """, new
            {
                number = Sequences.Next(tx, cfg.SequenceKey), partyId, date, amount = Money.ToPaisa(amount), method,
                accountId = account.Id, referenceNumber = referenceNumber.Trim(), notes = notes.Trim(), actor.UserId,
            });
        var payment = GetPayment(tx, cfg, id);
        AuditLog.Record(tx, actor, AuditAction.Create, cfg.PaymentModel, id, payment.Number, after: AuditLog.Snapshot(payment));
        if (allocations is { Count: > 0 })
            AllocateInternal(tx, actor, cfg, id, allocations);
        else if (autoAllocate)
            AutoAllocateInternal(tx, actor, cfg, id);
        return GetPayment(tx, cfg, id);
    }

    /// <summary>
    /// Allocate a payment across several invoices/bills. Each allocation is checked against the
    /// document's open balance and the payment's unallocated amount.
    /// </summary>
    public static IReadOnlyList<Allocation> Allocate(Tx tx, Actor actor, AccountConfig cfg, long paymentId, IReadOnlyList<AllocationRequest> allocations)
    {
        Permissions.Require(actor, cfg.Permission + ".create");
        return AllocateInternal(tx, actor, cfg, paymentId, allocations);
    }

    private static IReadOnlyList<Allocation> AllocateInternal(Tx tx, Actor actor, AccountConfig cfg, long paymentId, IReadOnlyList<AllocationRequest> allocations)
    {
        tx.RequireWrite();
        var payment = GetPayment(tx, cfg, paymentId);
        if (payment.IsCancelled)
            throw new AllocationError($"{payment.Number} is cancelled and cannot be allocated.");
        var requested = new SortedDictionary<long, decimal>();
        foreach (var item in allocations)
        {
            var amt = Money.Round(item.Amount);
            if (amt <= 0)
                throw new AllocationError("Allocation amounts must be greater than zero.");
            requested[item.DocumentId] = requested.GetValueOrDefault(item.DocumentId) + amt;
        }
        if (requested.Count == 0)
            throw new AllocationError("Nothing to allocate.");
        var total = Money.Round(requested.Values.Sum());
        if (total > payment.UnallocatedAmount)
            throw new AllocationError($"Allocation total {total:0.00} exceeds the unallocated amount {payment.UnallocatedAmount:0.00} of {payment.Number}.");
        var created = new List<long>();
        foreach (var (docId, amt) in requested)
        {
            var doc = TradeService.Find(tx, cfg.Documents, docId);
            if (doc is null || doc.PartyId != payment.PartyId)
                throw new AllocationError($"{cfg.DocLabel} #{docId} does not belong to this {cfg.PartyType.ToCode().ToLowerInvariant()}.");
            if (doc.IsCancelled)
                throw new AllocationError($"{doc.Number} is cancelled.");
            if (amt > doc.BalanceDue)
                throw new AllocationError($"Allocation {amt:0.00} exceeds the outstanding balance {doc.BalanceDue:0.00} of {doc.Number}.");
            created.Add(tx.Insert(
                $"INSERT INTO {cfg.AllocationTable} (payment_id, document_id, amount, date, created_by) VALUES (@paymentId, @docId, @amount, @date, @UserId)",
                new { paymentId, docId, amount = Money.ToPaisa(amt), date = payment.Date > doc.Date ? payment.Date : doc.Date, actor.UserId }));
            RefreshDocument(tx, cfg, docId);
        }
        RefreshPayment(tx, cfg, paymentId);
        var result = created.Select(id => GetAllocation(tx, cfg, id)).ToList();
        AuditLog.Record(tx, actor, AuditAction.Allocate, cfg.PaymentModel, paymentId, payment.Number,
            after: AuditLog.Snapshot(new { allocations = result.Select(a => new { document = a.DocumentNumber, amount = a.Amount }) }));
        return result;
    }

    /// <summary>Active documents of the party with something still to pay, oldest due first.</summary>
    public static IReadOnlyList<TradeDocument> OpenDocuments(Tx tx, AccountConfig cfg, long partyId) =>
        tx.Query<long>($"SELECT id FROM {cfg.DocTable} WHERE party_id = @partyId AND status = 'ACTIVE' AND amount_paid < total_amount ORDER BY due_date, date, id",
                new { partyId })
            .ToList().Select(id => TradeService.Get(tx, cfg.Documents, id)).ToList();

    /// <summary>Allocate the unallocated part of a payment to the oldest-due documents first.</summary>
    public static IReadOnlyList<Allocation> AutoAllocate(Tx tx, Actor actor, AccountConfig cfg, long paymentId)
    {
        Permissions.Require(actor, cfg.Permission + ".create");
        return AutoAllocateInternal(tx, actor, cfg, paymentId);
    }

    private static IReadOnlyList<Allocation> AutoAllocateInternal(Tx tx, Actor actor, AccountConfig cfg, long paymentId)
    {
        var payment = GetPayment(tx, cfg, paymentId);
        var remaining = payment.UnallocatedAmount;
        var plan = new List<AllocationRequest>();
        foreach (var doc in OpenDocuments(tx, cfg, payment.PartyId))
        {
            if (remaining <= 0)
                break;
            var amt = Math.Min(remaining, doc.BalanceDue);
            if (amt > 0)
            {
                plan.Add(new AllocationRequest(doc.Id, amt));
                remaining -= amt;
            }
        }
        return plan.Count > 0 ? AllocateInternal(tx, actor, cfg, paymentId, plan) : [];
    }

    private static void Void(Tx tx, Actor actor, AccountConfig cfg, long allocationId, string reason) =>
        tx.Execute(
            $"""
            UPDATE {cfg.AllocationTable} SET is_active = 0, voided_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), voided_on = @today,
                voided_by = @UserId, void_reason = @reason
            WHERE id = @allocationId
            """, new { allocationId, today = tx.Today, actor.UserId, reason = reason.Length > 255 ? reason[..255] : reason });

    public static PartyPayment Unallocate(Tx tx, Actor actor, AccountConfig cfg, long allocationId, string reason)
    {
        Permissions.Require(actor, cfg.Permission + ".create");
        var alloc = tx.Rows($"SELECT id FROM {cfg.AllocationTable} WHERE id = @allocationId AND is_active = 1", new { allocationId }).Any()
            ? GetAllocation(tx, cfg, allocationId)
            : throw new AllocationError("Allocation not found or already removed.");
        Void(tx, actor, cfg, allocationId, string.IsNullOrWhiteSpace(reason) ? "Unallocated" : reason.Trim());
        RefreshDocument(tx, cfg, alloc.DocumentId);
        RefreshPayment(tx, cfg, alloc.PaymentId);
        AuditLog.Record(tx, actor, AuditAction.Update, cfg.PaymentModel, alloc.PaymentId, alloc.PaymentNumber,
            AuditLog.Snapshot(new { allocation = alloc.Id, document = alloc.DocumentNumber, amount = alloc.Amount }),
            AuditLog.Snapshot(new { unallocated = true, reason }));
        return GetPayment(tx, cfg, alloc.PaymentId);
    }

    /// <summary>Called when an invoice/bill is cancelled: its payments become unallocated (advances).</summary>
    internal static void ReleaseDocumentAllocations(Tx tx, Actor actor, AccountConfig cfg, TradeDocument doc)
    {
        var allocs = tx.Rows($"SELECT id, payment_id FROM {cfg.AllocationTable} WHERE document_id = @id AND is_active = 1", new { id = doc.Id }).ToList();
        foreach (var a in allocs)
            Void(tx, actor, cfg, a.Long("id"), $"{doc.Number} cancelled");
        foreach (var paymentId in allocs.Select(a => a.Long("payment_id")).Distinct().Order())
            RefreshPayment(tx, cfg, paymentId);
        RefreshDocument(tx, cfg, doc.Id);
    }

    public static PartyPayment CancelPayment(Tx tx, Actor actor, AccountConfig cfg, long paymentId, string reason)
    {
        Permissions.Require(actor, cfg.Permission + ".cancel");
        tx.RequireWrite();
        var before = GetPayment(tx, cfg, paymentId);
        if (before.IsCancelled)
            throw new BusinessError($"{before.Number} is already cancelled.", "ALREADY_CANCELLED");
        if (string.IsNullOrWhiteSpace(reason))
            throw new BusinessError("A cancellation reason is required.", "REASON_REQUIRED");
        var allocs = tx.Rows($"SELECT id, document_id FROM {cfg.AllocationTable} WHERE payment_id = @paymentId AND is_active = 1", new { paymentId }).ToList();
        foreach (var a in allocs)
            Void(tx, actor, cfg, a.Long("id"), $"{before.Number} cancelled");
        foreach (var docId in allocs.Select(a => a.Long("document_id")).Distinct().Order())
            RefreshDocument(tx, cfg, docId);
        RefreshPayment(tx, cfg, paymentId);
        tx.Execute(
            $"""
            UPDATE {cfg.PaymentTable} SET status = 'CANCELLED', cancelled_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), cancelled_on = @today,
                cancelled_by = @UserId, cancel_reason = @reason, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @paymentId
            """, new { paymentId, today = tx.Today, actor.UserId, reason = reason.Trim() });
        var after = GetPayment(tx, cfg, paymentId);
        AuditLog.Record(tx, actor, AuditAction.Cancel, cfg.PaymentModel, paymentId, after.Number, AuditLog.Snapshot(before), AuditLog.Snapshot(after));
        return after;
    }

    /// <summary>
    /// Record a payment made together with the invoice/bill and allocate it to that document.
    /// Allowed for anyone who may create the document (staff take cash on a sale).
    /// </summary>
    internal static PartyPayment? PayOnCreation(Tx tx, Actor actor, AccountConfig cfg, TradeDocument doc, PaymentInput payment)
    {
        var amount = payment.PayInFull ? doc.TotalAmount : Money.Round(payment.Amount ?? 0);
        if (amount <= 0)
            return null;
        if (amount > doc.TotalAmount)
            throw new BusinessError($"Amount paid ({amount:0.00}) cannot exceed the document total ({doc.TotalAmount:0.00}).", "INVALID_AMOUNT");
        return Insert(tx, actor, cfg, doc.PartyId, doc.Date, amount, payment.PaymentMethod, payment.AccountId, payment.ReferenceNumber,
            $"Paid against {doc.Number}", [new AllocationRequest(doc.Id, amount)], autoAllocate: false);
    }

    // ------------------------------------------------------------------ queries

    private const string PaymentSelect =
        """
        SELECT p.*, pt.name AS party_name, a.name AS account_name FROM {0} p
        JOIN parties pt ON pt.id = p.party_id
        JOIN money_accounts a ON a.id = p.account_id
        """;

    public static PartyPayment GetPayment(Tx tx, AccountConfig cfg, long id) =>
        tx.Rows(string.Format(PaymentSelect, cfg.PaymentTable) + " WHERE p.id = @id", new { id }).Select(r => MapPayment(cfg, r)).FirstOrDefault()
        ?? throw new NotFoundError($"{cfg.PayLabel} not found.");

    public static Page<PartyPayment> ListPayments(Tx tx, AccountConfig cfg, PaymentFilter filter, PageRequest? page = null)
    {
        page ??= PageRequest.First;
        const string where =
            """
            WHERE (@start IS NULL OR p.date >= @start) AND (@end IS NULL OR p.date <= @end)
              AND (@partyId IS NULL OR p.party_id = @partyId) AND (@status IS NULL OR p.status = @status)
              AND (@accountId IS NULL OR p.account_id = @accountId) AND (@method IS NULL OR p.payment_method = @method)
              AND (@unallocated = 0 OR (p.status = 'ACTIVE' AND p.allocated_amount < p.amount))
              AND (@search IS NULL OR p.number LIKE @search OR p.reference_number LIKE @search OR pt.name LIKE @search)
            """;
        var args = new
        {
            start = filter.Start, end = filter.End, partyId = filter.PartyId, status = filter.Status, accountId = filter.AccountId,
            method = filter.PaymentMethod, unallocated = filter.UnallocatedOnly,
            search = ProductService.LikePattern(filter.Search), limit = page.Size, offset = page.Offset,
        };
        var select = string.Format(PaymentSelect, cfg.PaymentTable);
        var count = tx.Scalar<long>($"SELECT COUNT(*) FROM ({select} {where})", args);
        var rows = tx.Rows($"{select} {where} ORDER BY p.date DESC, p.id DESC LIMIT @limit OFFSET @offset", args)
            .Select(r => MapPayment(cfg, r)).ToList();
        return new Page<PartyPayment>(rows, (int)count, page.Page, page.Size);
    }

    private const string AllocationSelect =
        """
        SELECT al.*, p.number AS payment_number, d.number AS document_number FROM {0} al
        JOIN {1} p ON p.id = al.payment_id
        JOIN {2} d ON d.id = al.document_id
        """;

    private static string AllocationSql(AccountConfig cfg) => string.Format(AllocationSelect, cfg.AllocationTable, cfg.PaymentTable, cfg.DocTable);

    public static Allocation GetAllocation(Tx tx, AccountConfig cfg, long id) =>
        tx.Rows(AllocationSql(cfg) + " WHERE al.id = @id", new { id }).Select(MapAllocation).First();

    public static IReadOnlyList<Allocation> AllocationsForPayment(Tx tx, AccountConfig cfg, long paymentId) =>
        tx.Rows(AllocationSql(cfg) + " WHERE al.payment_id = @paymentId ORDER BY al.date, al.id", new { paymentId }).Select(MapAllocation).ToList();

    public static IReadOnlyList<Allocation> AllocationsForDocument(Tx tx, AccountConfig cfg, long documentId) =>
        tx.Rows(AllocationSql(cfg) + " WHERE al.document_id = @documentId ORDER BY al.date, al.id", new { documentId }).Select(MapAllocation).ToList();

    private static PartyPayment MapPayment(AccountConfig cfg, Row r) => new()
    {
        Id = r.Long("id"),
        Config = cfg,
        Number = r.Str("number"),
        PartyId = r.Long("party_id"),
        PartyName = r.Str("party_name"),
        Date = r.Date("date"),
        Amount = r.Money("amount"),
        AllocatedAmount = r.Money("allocated_amount"),
        PaymentMethod = r.Str("payment_method"),
        AccountId = r.Long("account_id"),
        AccountName = r.Str("account_name"),
        ReferenceNumber = r.Str("reference_number"),
        Notes = r.Str("notes"),
        Status = r.Str("status"),
        CancelledAt = r.NullableStr("cancelled_at") is null ? null : r.Timestamp("cancelled_at"),
        CancelledOn = r.NullableDate("cancelled_on"),
        CancelReason = r.Str("cancel_reason"),
        CreatedBy = r.Long("created_by"),
        CreatedAt = r.Timestamp("created_at"),
    };

    private static Allocation MapAllocation(Row r) => new(
        r.Long("id"), r.Long("payment_id"), r.Str("payment_number"), r.Long("document_id"), r.Str("document_number"),
        r.Money("amount"), r.Date("date"), r.Bool("is_active"),
        r.NullableStr("voided_at") is null ? null : r.Timestamp("voided_at"), r.NullableDate("voided_on"), r.Str("void_reason"));
}
