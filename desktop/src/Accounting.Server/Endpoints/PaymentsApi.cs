using Accounting.Core;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Reports;
using Accounting.Core.Trade;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>
/// /api/customer-receipts/ and /api/vendor-payments/ (list, detail, create, allocate, auto-allocate,
/// unallocate, cancel, PDF, open documents) and the party ledger / statement / aging / summary.
/// </summary>
public static class PaymentsApi
{
    private static string NumberKey(AccountConfig cfg) => cfg.Documents.IsSale ? "receipt_number" : "payment_number";

    public static Dictionary<string, object?> PaymentJson(Tx tx, AccountConfig cfg, PartyPayment p, bool detail)
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = p.Id, ["number"] = p.Number, [NumberKey(cfg)] = p.Number, ["party"] = p.PartyId, ["party_name"] = p.PartyName,
            ["date"] = Json.D(p.Date), ["amount"] = Json.M(p.Amount), ["allocated_amount"] = Json.M(p.AllocatedAmount),
            ["unallocated_amount"] = Json.M(p.UnallocatedAmount), ["payment_method"] = p.PaymentMethod,
            ["payment_method_display"] = PaymentMethod.Display(p.PaymentMethod), ["account"] = p.AccountId, ["account_name"] = p.AccountName,
            ["reference_number"] = p.ReferenceNumber, ["notes"] = p.Notes, ["status"] = p.Status, ["created_at"] = Json.T(p.CreatedAt),
            ["created_by_name"] = tx.Scalar<string>("SELECT username FROM users WHERE id = @id", new { id = p.CreatedBy }),
        };
        if (!detail)
            return data;
        var party = PartyService.Get(tx, p.PartyId);
        data["cancelled_at"] = Json.T(p.CancelledAt);
        data["cancel_reason"] = p.CancelReason;
        data["party_pan_vat_no"] = party.PanVatNo;
        data["party_address"] = party.Address;
        data["allocations"] = PartyAccounts.AllocationsForPayment(tx, cfg, p.Id).OrderBy(a => a.Id).Select(a =>
        {
            var doc = TradeService.Get(tx, cfg.Documents, a.DocumentId);
            return new
            {
                id = a.Id, document_id = a.DocumentId, document_number = a.DocumentNumber, document_date = Json.D(doc.Date),
                document_total = Json.M(doc.TotalAmount), amount = Json.M(a.Amount), date = Json.D(a.Date), is_active = a.IsActive,
                voided_at = Json.T(a.VoidedAt), void_reason = a.VoidReason,
            };
        }).ToList();
        return data;
    }

    public static object PartyHeader(Party p) => new
    {
        id = p.Id, name = p.Name, type = p.Type.ToCode(), pan_vat_no = p.PanVatNo, phone = p.Phone, email = p.Email, address = p.Address,
        credit_terms = p.CreditTerms.Display(), credit_days = p.CreditDays, credit_limit = Json.M(p.CreditLimit),
    };

    public static object LedgerEntryJson(LedgerEntry e) => new
    {
        date = Json.D(e.Date), reference = e.Reference, reference_id = e.ReferenceId, reference_type = e.ReferenceType,
        transaction_type = e.TransactionType, description = e.Description, debit = Json.M(e.Debit), credit = Json.M(e.Credit),
        due_date = Json.D(e.DueDate), payment_status = e.PaymentStatus, status = e.Status, balance = Json.M(e.Balance),
    };

    public static Dictionary<string, object?> AmountsJson(AgingAmounts a)
    {
        var data = AgingBucket.All.ToDictionary(b => b.Key, b => (object?)Json.M(a[b.Key]));
        data["total"] = Json.M(a.Total);
        data["unallocated"] = Json.M(a.Unallocated);
        data["journal"] = Json.M(a.Journal);
        data["opening"] = Json.M(a.Opening);
        data["net_balance"] = Json.M(a.NetBalance);
        return data;
    }

    public static object Buckets => AgingBucket.All.Select(b => new { key = b.Key, label = b.Label });

    private static long Allocation(System.Text.Json.JsonElement b) => b.ReqLong("allocation");

    public static void Map(RouteGroupBuilder api)
    {
        foreach (var (route, cfg) in new[] { ("customer-receipts", AccountConfig.Receivable), ("vendor-payments", AccountConfig.Payable) })
        {
            var view = cfg.Permission + ".view";
            var label = cfg.Documents.IsSale ? "Receipt" : "Payment";

            api.MapGet(route, (HttpContext h) =>
            {
                var a = Api.Of(h);
                a.Require(view);
                var filter = new PaymentFilter
                {
                    PartyId = a.Int("party"), Status = a.Q("status"), AccountId = a.Int("account"), PaymentMethod = a.Q("payment_method"),
                    UnallocatedOnly = a.Q("unallocated") == "true", Start = a.Date("start_date"), End = a.Date("end_date"), Search = a.Q("search"),
                };
                var (page, rows) = a.Read(tx =>
                {
                    var p = PartyAccounts.ListPayments(tx, cfg, filter, a.Page);
                    return (p, p.Results.Select(x => (object)PaymentJson(tx, cfg, x, false)).ToList());
                });
                return Api.Paged(new Page<object>(rows, page.Count, page.PageNumber, page.PageSize), x => x);
            });

            api.MapGet($"{route}/open-documents", (HttpContext h) =>
            {
                var a = Api.Of(h);
                a.Require(view);
                var party = a.Int("party") ?? throw new ValidationError("party", "This parameter is required.");
                var rows = a.Read(tx => PartyAccounts.OpenDocuments(tx, cfg, party).Take(500).Select(d => new
                {
                    id = d.Id, number = d.Number, date = Json.D(d.Date), due_date = Json.D(d.DueDate), total_amount = Json.M(d.TotalAmount),
                    amount_paid = Json.M(d.AmountPaid), balance_due = Json.M(d.BalanceDue), payment_status = d.PaymentStatus,
                    days_overdue = Math.Max(tx.Today.DayNumber - d.DueDate.DayNumber, 0),
                }).ToList());
                return Api.Ok(rows);
            });

            api.MapGet($"{route}/{{id:long}}", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                a.Require(view);
                return Api.Ok(a.Read(tx => PaymentJson(tx, cfg, PartyAccounts.GetPayment(tx, cfg, id), true)));
            });

            api.MapPost(route, async (HttpContext h) =>
            {
                var a = Api.Of(h);
                var b = await a.BodyAsync();
                var amount = b.ReqDec("amount");
                if (amount < 0.01m)
                    throw new ValidationError("amount", "Ensure this value is greater than or equal to 0.01.");
                var method = b.Str("payment_method");
                if (method is not null && !PaymentMethod.All.Contains(method))
                    throw new ValidationError("payment_method", $"\"{method}\" is not a valid choice.");
                var allocations = b.List("allocations").Select(x => new AllocationRequest(x.ReqLong("document"), x.ReqDec("amount"))).ToList();
                var payment = a.Write((tx, actor) => PartyAccounts.CreatePayment(tx, actor, cfg, b.ReqLong("party"), b.ReqDate("date"), amount, method,
                    b.Long("account"), b.Str("reference_number") ?? "", b.Str("notes") ?? "", allocations.Count > 0 ? allocations : null, b.Bool("auto_allocate") ?? false,
                    h.Items.ContainsKey("internal") ? b.Str("number") : null));
                return Api.Created(a.Read(tx => PaymentJson(tx, cfg, payment, true)), $"{label} {payment.Number} recorded successfully.");
            });

            api.MapPost($"{route}/{{id:long}}/allocate", async (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var b = await a.BodyAsync();
                var allocations = b.List("allocations").Select(x => new AllocationRequest(x.ReqLong("document"), x.ReqDec("amount"))).ToList();
                a.Write((tx, actor) => PartyAccounts.Allocate(tx, actor, cfg, id, allocations));
                var p = a.Read(tx => PartyAccounts.GetPayment(tx, cfg, id));
                return Api.Ok(a.Read(tx => PaymentJson(tx, cfg, p, true)), $"{p.Number} allocated.");
            });

            api.MapPost($"{route}/{{id:long}}/auto-allocate", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var made = a.Write((tx, actor) => PartyAccounts.AutoAllocate(tx, actor, cfg, id));
                return Api.Ok(a.Read(tx => PaymentJson(tx, cfg, PartyAccounts.GetPayment(tx, cfg, id), true)),
                    made.Count > 0 ? $"Allocated to {made.Count} document(s)." : "No open documents to allocate to.");
            });

            api.MapPost($"{route}/{{id:long}}/unallocate", async (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var b = await a.BodyAsync();
                var allocationId = Allocation(b);
                if (!a.Read(tx => PartyAccounts.AllocationsForPayment(tx, cfg, id).Any(x => x.Id == allocationId)))
                    throw new AllocationError("Allocation does not belong to this payment.");
                a.Write((tx, actor) => PartyAccounts.Unallocate(tx, actor, cfg, allocationId, b.Str("reason") ?? ""));
                return Api.Ok(a.Read(tx => PaymentJson(tx, cfg, PartyAccounts.GetPayment(tx, cfg, id), true)), "Allocation removed.");
            });

            api.MapPost($"{route}/{{id:long}}/cancel", async (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var b = await a.BodyAsync();
                var p = a.Write((tx, actor) => PartyAccounts.CancelPayment(tx, actor, cfg, id, b.ReqStr("reason")));
                return Api.Ok(a.Read(tx => PaymentJson(tx, cfg, p, true)), $"{p.Number} cancelled.");
            });

            api.MapGet($"{route}/{{id:long}}/pdf", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                a.Require(view);
                var (pdf, number) = a.Read(tx => (DocumentPdf.Payment(tx, cfg, id), PartyAccounts.GetPayment(tx, cfg, id).Number));
                return Api.File(pdf, "application/pdf", $"{number}.pdf", inline: a.Q("inline") == "1");
            });
        }

        // ------------------------------------------------------------------ party ledger, statement, aging, summary
        foreach (var (route, cfg) in new[] { ("customers", AccountConfig.Receivable), ("vendors", AccountConfig.Payable) })
        {
            Party PartyOf(Api a, long id) => a.Read(tx => PartyService.Get(tx, id, cfg.PartyType));

            api.MapGet($"{route}/{{id:long}}/ledger", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                a.Require("ledgers.view");
                var party = PartyOf(a, id);
                var (start, end) = (a.Date("start_date"), a.Date("end_date"));
                if (a.Q("export") is { } kind)
                    return ReportsApi.Export(a, kind, a.Read(tx => DocumentPdf.Statement(tx, cfg, id, start, end)));
                var ledger = a.Read(tx => PartyLedgers.Ledger(tx, cfg, id, start, end));
                return a.PagedList(ledger.Entries, LedgerEntryJson, new Dictionary<string, object?>
                {
                    ["party"] = PartyHeader(party), ["opening_balance"] = Json.M(ledger.OpeningBalance), ["closing_balance"] = Json.M(ledger.ClosingBalance),
                    ["total_debit"] = Json.M(ledger.TotalDebit), ["total_credit"] = Json.M(ledger.TotalCredit),
                    ["start_date"] = Json.D(ledger.Start), ["end_date"] = Json.D(ledger.End),
                });
            });

            api.MapGet($"{route}/{{id:long}}/statement", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                a.Require("ledgers.view");
                var party = PartyOf(a, id);
                var end = a.Date("end_date") ?? a.Db.Today();
                var start = a.Date("start_date") ?? new DateOnly(end.Year, end.Month, 1);
                if (a.Q("export") is { } kind)
                    return ReportsApi.Export(a, kind, a.Read(tx => DocumentPdf.Statement(tx, cfg, id, start, end)));
                var ledger = a.Read(tx => PartyLedgers.Ledger(tx, cfg, id, start, end));
                return Api.Ok(new
                {
                    party = PartyHeader(party), opening_balance = Json.M(ledger.OpeningBalance), closing_balance = Json.M(ledger.ClosingBalance),
                    total_debit = Json.M(ledger.TotalDebit), total_credit = Json.M(ledger.TotalCredit), start_date = Json.D(ledger.Start),
                    end_date = Json.D(ledger.End), entries = ledger.Entries.Select(LedgerEntryJson).ToList(),
                });
            });

            api.MapGet($"{route}/{{id:long}}/aging", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                a.Require("ledgers.view");
                var party = PartyOf(a, id);
                var asOf = a.Date("as_of") ?? a.Db.Today();
                var d = a.Read(tx => PartyLedgers.AgingDetail(tx, cfg, id, asOf));
                return Api.Ok(new
                {
                    party = PartyHeader(party), as_of = Json.D(asOf), buckets = Buckets, totals = AmountsJson(d.Totals),
                    documents = d.Documents.Select(x => new
                    {
                        id = x.Id, number = x.Number, date = Json.D(x.Date), due_date = Json.D(x.DueDate), total_amount = Json.M(x.TotalAmount),
                        paid_as_of = Json.M(x.PaidAsOf), outstanding = Json.M(x.Outstanding), days_overdue = x.DaysOverdue, bucket = x.Bucket,
                    }).ToList(),
                    unallocated_payments = d.UnallocatedPayments.Select(p => new
                    {
                        id = p.Id, number = p.Number, date = Json.D(p.Date), amount = Json.M(p.Amount), unallocated = Json.M(p.Unallocated),
                    }).ToList(),
                });
            });

            api.MapGet($"{route}/{{id:long}}/summary", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                a.Require("parties.view");
                var party = PartyOf(a, id);
                var s = a.Read(tx => PartyLedgers.Summary(tx, cfg, party));
                return Api.Ok(new
                {
                    party = PartyHeader(party), party_id = s.PartyId, total_documents = Json.M(s.TotalDocuments), document_count = s.DocumentCount,
                    total_payments = Json.M(s.TotalPayments), payment_count = s.PaymentCount, outstanding = Json.M(s.Outstanding), overdue = Json.M(s.Overdue),
                    current = Json.M(s.Current), unallocated = Json.M(s.Unallocated), journal = Json.M(s.Journal), opening = Json.M(s.Opening),
                    net_balance = Json.M(s.NetBalance), last_transaction_date = Json.D(s.LastTransactionDate), credit_limit = Json.M(s.CreditLimit),
                    available_credit = Json.M(s.AvailableCredit),
                });
            });
        }
    }
}
