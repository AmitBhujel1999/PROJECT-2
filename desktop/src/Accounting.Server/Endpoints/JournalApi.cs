using Accounting.Core;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Journal;
using Accounting.Core.Reports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>/api/journal/ledgers/ (chart of accounts, ledger book) and /api/journal/vouchers/.</summary>
public static class JournalApi
{
    public static object DrCrJson(DrCr v) => new { amount = Json.M(v.Amount), side = v.Side };

    private static object LedgerJson(Tx tx, LedgerAccount l, DrCr balance)
    {
        var times = tx.Row("SELECT created_at, updated_at FROM ledger_accounts WHERE id = @id", new { id = l.Id })!;
        return new
        {
            id = l.Id, name = l.Name, group = l.Group, group_display = l.GroupLabel, nature = l.Nature, opening_balance = Json.M(l.OpeningBalance),
            opening_side = l.OpeningSide, notes = l.Notes, is_active = l.IsActive, balance = DrCrJson(balance),
            created_at = Json.T(times.Timestamp("created_at")), updated_at = Json.T(times.Timestamp("updated_at")),
        };
    }

    private static object Ledger(Api a, long id) => a.Read(tx => LedgerJson(tx, JournalService.GetLedger(tx, id), JournalService.LedgerBalances(tx).GetValueOrDefault(id)));

    private static object AccountJson(AccountOption o) => new { key = o.Key, name = o.Name, group = o.Group };

    private static Dictionary<string, object?> VoucherJson(Tx tx, JournalVoucher v, bool list)
    {
        var r = tx.Row(
            """
            SELECT j.cancelled_at, u.username AS created_by_name, x.username AS cancelled_by_name FROM journal_vouchers j
            LEFT JOIN users u ON u.id = j.created_by LEFT JOIN users x ON x.id = j.cancelled_by WHERE j.id = @id
            """, new { id = v.Id })!;
        var data = new Dictionary<string, object?>
        {
            ["id"] = v.Id, ["voucher_number"] = v.Number, ["date"] = Json.D(v.Date), ["narration"] = v.Narration, ["total_amount"] = Json.M(v.TotalAmount),
            ["status"] = v.Status, ["created_at"] = Json.T(v.CreatedAt),
        };
        if (list)
        {
            data["accounts"] = new { debit = v.DebitAccounts.ToList(), credit = v.CreditAccounts.ToList() };
            return data;
        }
        data["cancelled_at"] = r.NullableStr("cancelled_at") is null ? null : Json.T(r.Timestamp("cancelled_at"));
        data["cancel_reason"] = v.CancelReason;
        data["created_by_name"] = r.NullableStr("created_by_name");
        data["cancelled_by_name"] = r.NullableStr("cancelled_by_name");
        data["lines"] = v.Lines.Select(l => new { line_no = l.LineNo, account = AccountJson(l.Account), debit = Json.M(l.Debit), credit = Json.M(l.Credit), narration = l.Narration }).ToList();
        return data;
    }

    private static LedgerInput Input(System.Text.Json.JsonElement b, LedgerAccount? current)
    {
        var amount = b.DecOr("opening_balance", current?.OpeningBalance ?? 0);
        if (amount < 0)
            throw new ValidationError("opening_balance", "Enter the amount and choose Dr or Cr.");
        return new LedgerInput(
            current is null ? b.ReqStr("name") : b.StrOr("name", current.Name),
            current is null ? b.ReqStr("group") : b.StrOr("group", current.Group),
            amount,
            b.Has("opening_side") ? b.Str("opening_side") : current?.OpeningSide,
            b.StrOr("notes", current?.Notes ?? ""));
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("journal/ledgers/groups", (HttpContext h) =>
        {
            Api.Of(h).Require("journal.view");
            return Api.Ok(AccountGroup.All.Select(g => new { value = g.Code, label = g.Label, nature = g.Nature }));
        });
        api.MapGet("journal/ledgers", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("journal.view");
            var group = a.Q("group");
            var rows = a.Read(tx =>
            {
                var balances = JournalService.LedgerBalances(tx);
                return JournalService.Ledgers(tx, a.Bool("is_active"), a.Q("search"))
                    .Where(l => group is null || l.Group == group)
                    .OrderBy(l => a.Q("ordering")?.TrimStart('-') == "group" ? l.Group : l.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(l => LedgerJson(tx, l, balances.GetValueOrDefault(l.Id))).ToList();
            });
            return a.PagedList(rows, x => x);
        });
        api.MapGet("journal/ledgers/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("journal.view");
            return Api.Ok(Ledger(a, id));
        });
        api.MapPost("journal/ledgers", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var input = Input(await a.BodyAsync(), null);
            var l = a.Write((tx, actor) => JournalService.CreateLedger(tx, actor, input));
            return Api.Created(Ledger(a, l.Id), $"Ledger {l.Name} created.");
        });
        api.MapMethods("journal/ledgers/{id:long}", ["PUT", "PATCH"], async (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var current = a.Read(tx => JournalService.GetLedger(tx, id));
            var l = a.Write((tx, actor) => JournalService.UpdateLedger(tx, actor, id, Input(b, current)));
            return Api.Ok(Ledger(a, id), $"Ledger {l.Name} updated.");
        });
        api.MapDelete("journal/ledgers/{id:long}", (HttpContext h, long id) =>
        {
            Api.Of(h).Write((tx, actor) => { JournalService.DeleteLedger(tx, actor, id); return 0; });
            return Api.Ok(null, "Ledger deleted.");
        });
        foreach (var (verb, active) in new[] { ("activate", true), ("deactivate", false) })
            api.MapPost($"journal/ledgers/{{id:long}}/{verb}", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var l = a.Write((tx, actor) => JournalService.SetLedgerActive(tx, actor, id, active));
                return Api.Ok(Ledger(a, id), $"{l.Name} {verb}d.");
            });
        api.MapGet("journal/ledgers/{id:long}/book", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("journal.view");
            var book = a.Read(tx => JournalService.Book(tx, id, a.Date("start_date"), a.Date("end_date")));
            if (a.Q("export") is { } kind)
                return ReportsApi.Export(a, kind, ReportsApi.LedgerBookTable(a, book));
            return Api.Ok(new
            {
                ledger = book.Ledger.Id, ledger_name = book.Ledger.Name, group_display = book.Ledger.GroupLabel, opening = DrCrJson(book.Opening),
                total_debit = Json.M(book.TotalDebit), total_credit = Json.M(book.TotalCredit), closing = DrCrJson(book.Closing),
                start_date = Json.D(book.Start), end_date = Json.D(book.End),
                entries = book.Entries.Select(e => new
                {
                    date = Json.D(e.Date), voucher_id = e.VoucherId, number = e.Number, narration = e.Narration, debit = Json.M(e.Debit),
                    credit = Json.M(e.Credit), balance = DrCrJson(e.Balance),
                }).ToList(),
            });
        });

        // ------------------------------------------------------------------ vouchers
        api.MapGet("journal/vouchers/accounts", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("journal.create");
            var search = a.Q("search");
            return Api.Ok(a.Read(tx => JournalService.AccountOptions(tx, search is null ? null : search[..Math.Min(search.Length, 100)]).Select(AccountJson).ToList()));
        });
        api.MapGet("journal/vouchers", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("journal.view");
            var account = a.Q("account");
            var (page, rows) = a.Read(tx =>
            {
                var p = JournalService.ListVouchers(tx, a.Date("start_date"), a.Date("end_date"), a.Q("status"), a.Q("search"), account is null ? a.Page : new PageRequest(1, 100));
                var vouchers = p.Results.AsEnumerable();
                if (account is not null)
                {
                    // Account filter: vouchers with a line on that account (filtered after reading all of them).
                    var all = new List<JournalVoucher>();
                    for (var n = 1; ; n++)
                    {
                        var part = JournalService.ListVouchers(tx, a.Date("start_date"), a.Date("end_date"), a.Q("status"), a.Q("search"), new PageRequest(n, 100));
                        all.AddRange(part.Results);
                        if (n >= part.TotalPages)
                            break;
                    }
                    var matching = all.Where(v => v.Lines.Any(l => l.Account.Key == account)).ToList();
                    p = new Page<JournalVoucher>(matching.Skip(a.Page.Offset).Take(a.Page.Size).ToList(), matching.Count, a.Page.Page, a.Page.Size);
                    vouchers = p.Results;
                }
                return (p, vouchers.Select(v => (object)VoucherJson(tx, v, list: true)).ToList());
            });
            return Api.Paged(new Page<object>(rows, page.Count, page.PageNumber, page.PageSize), x => x);
        });
        api.MapGet("journal/vouchers/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("journal.view");
            return Api.Ok(a.Read(tx => VoucherJson(tx, JournalService.GetVoucher(tx, id), list: false)));
        });
        api.MapPost("journal/vouchers", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var lines = b.List("lines").Select(l => new JournalLineInput(l.Str("account") ?? "", l.Dec("debit") ?? 0, l.Dec("credit") ?? 0, l.Str("narration") ?? "")).ToList();
            var v = a.Write((tx, actor) => JournalService.CreateVoucher(tx, actor, b.ReqDate("date"), b.Str("narration") ?? "", lines));
            return Api.Created(a.Read(tx => VoucherJson(tx, v, list: false)), $"Journal voucher {v.Number} recorded successfully.");
        });
        api.MapPost("journal/vouchers/{id:long}/cancel", async (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var v = a.Write((tx, actor) => JournalService.CancelVoucher(tx, actor, id, b.ReqStr("reason")));
            return Api.Ok(a.Read(tx => VoucherJson(tx, v, list: false)), $"Journal voucher {v.Number} cancelled.");
        });
        api.MapGet("journal/vouchers/{id:long}/pdf", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("journal.view");
            var (pdf, number) = a.Read(tx => (DocumentPdf.Journal(tx, id), JournalService.GetVoucher(tx, id).Number));
            return Api.File(pdf, "application/pdf", $"{number}.pdf", inline: a.Q("inline") == "1");
        });
    }
}
