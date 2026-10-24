using Accounting.Core;
using Accounting.Core.CashBank;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Reports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>/api/cash-bank/accounts/ (with account book), /api/cash-bank/contras/ and /api/reports/day-book/.</summary>
public static class CashBankApi
{
    private static object AccountJson(Tx tx, MoneyAccount a, decimal? balance)
    {
        var times = tx.Row("SELECT created_at, updated_at FROM money_accounts WHERE id = @id", new { id = a.Id })!;
        return new
        {
            id = a.Id, name = a.Name, kind = a.Kind, kind_display = AccountKind.Display(a.Kind), account_number = a.AccountNumber,
            opening_balance = Json.M(a.OpeningBalance), notes = a.Notes, is_active = a.IsActive, balance = Json.M(balance),
            created_at = Json.T(times.Timestamp("created_at")), updated_at = Json.T(times.Timestamp("updated_at")),
        };
    }

    private static object Account(Api a, long id) => a.Read(tx =>
        AccountJson(tx, MoneyAccounts.Get(tx, id), a.Can("cashbank.view") ? CashBook.Balance(tx, id) : null));

    private static object ContraJson(Tx tx, Contra c)
    {
        var r = tx.Row(
            """
            SELECT x.cancelled_at, x.created_at, u.username AS created_by_name, v.username AS cancelled_by_name FROM contras x
            LEFT JOIN users u ON u.id = x.created_by LEFT JOIN users v ON v.id = x.cancelled_by WHERE x.id = @id
            """, new { id = c.Id })!;
        return new
        {
            id = c.Id, contra_number = c.Number, date = Json.D(c.Date), from_account = c.FromAccountId, from_account_name = c.FromAccountName,
            to_account = c.ToAccountId, to_account_name = c.ToAccountName, amount = Json.M(c.Amount), reference_number = c.ReferenceNumber,
            notes = c.Notes, status = c.Status, cancelled_at = r.NullableStr("cancelled_at") is null ? null : Json.T(r.Timestamp("cancelled_at")),
            cancel_reason = c.CancelReason, created_at = Json.T(r.Timestamp("created_at")), created_by_name = r.NullableStr("created_by_name"),
            cancelled_by_name = r.NullableStr("cancelled_by_name"),
        };
    }

    private static MoneyAccount Input(System.Text.Json.JsonElement b, MoneyAccount? current) => new()
    {
        Name = current is null ? b.ReqStr("name") : b.StrOr("name", current.Name),
        Kind = b.StrOr("kind", current?.Kind ?? AccountKind.Bank),
        AccountNumber = b.StrOr("account_number", current?.AccountNumber ?? ""),
        OpeningBalance = b.DecOr("opening_balance", current?.OpeningBalance ?? 0),
        Notes = b.StrOr("notes", current?.Notes ?? ""),
    };

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("cash-bank/accounts", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("cashbank.list");
            var search = a.Q("search");
            var kind = a.Q("kind");
            var rows = a.Read(tx =>
            {
                var balances = a.Can("cashbank.view") ? CashBook.Balances(tx) : null;
                return MoneyAccounts.List(tx, a.Bool("is_active"))
                    .Where(x => kind is null || x.Kind == kind)
                    .Where(x => search is null || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || x.AccountNumber.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Select(x => AccountJson(tx, x, balances?.GetValueOrDefault(x.Id))).ToList();
            });
            return a.PagedList(rows, x => x);
        });
        api.MapGet("cash-bank/accounts/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("cashbank.list");
            return Api.Ok(Account(a, id));
        });
        api.MapPost("cash-bank/accounts", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var input = Input(await a.BodyAsync(), null);
            var account = a.Write((tx, actor) => MoneyAccounts.Create(tx, actor, input));
            return Api.Created(Account(a, account.Id), $"Account {account.Name} created.");
        });
        api.MapMethods("cash-bank/accounts/{id:long}", ["PUT", "PATCH"], async (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var current = a.Read(tx => MoneyAccounts.Get(tx, id));
            var account = a.Write((tx, actor) => MoneyAccounts.Update(tx, actor, id, Input(b, current)));
            return Api.Ok(Account(a, id), $"Account {account.Name} updated.");
        });
        api.MapDelete("cash-bank/accounts/{id:long}", (HttpContext h, long id) =>
        {
            Api.Of(h).Write((tx, actor) => { MoneyAccounts.Delete(tx, actor, id); return 0; });
            return Api.Ok(null, "Account deleted.");
        });
        foreach (var (verb, active) in new[] { ("activate", true), ("deactivate", false) })
            api.MapPost($"cash-bank/accounts/{{id:long}}/{verb}", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var account = a.Write((tx, actor) => MoneyAccounts.SetActive(tx, actor, id, active));
                return Api.Ok(Account(a, id), $"{account.Name} {verb}d.");
            });

        api.MapGet("cash-bank/accounts/{id:long}/book", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("cashbank.view");
            var (start, end) = (a.Date("start_date"), a.Date("end_date"));
            var book = a.Read(tx => CashBook.Book(tx, id, start, end));
            if (a.Q("export") is { } kind)
                return ReportsApi.Export(a, kind, ReportsApi.AccountBookTable(a, book));
            return Api.Ok(new
            {
                account = book.Account.Id, account_name = book.Account.Name, opening_balance = Json.M(book.OpeningBalance), total_in = Json.M(book.TotalIn),
                total_out = Json.M(book.TotalOut), closing_balance = Json.M(book.ClosingBalance), start_date = Json.D(book.Start), end_date = Json.D(book.End),
                entries = book.Entries.Select(e => new
                {
                    date = Json.D(e.Date), voucher_type = e.VoucherType, number = e.Number, object_id = e.ObjectId, particulars = e.Particulars,
                    reference_number = e.ReferenceNumber, money_in = Json.M(e.MoneyIn), money_out = Json.M(e.MoneyOut), balance = Json.M(e.Balance),
                }).ToList(),
            });
        });

        // ------------------------------------------------------------------ contra
        api.MapGet("cash-bank/contras", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("cashbank.view");
            var (page, rows) = a.Read(tx =>
            {
                var p = CashBook.ListContras(tx, a.Date("start_date"), a.Date("end_date"), a.Int("account"), a.Q("status"), a.Page);
                return (p, p.Results.Select(c => ContraJson(tx, c)).ToList());
            });
            return Api.Paged(new Page<object>(rows, page.Count, page.PageNumber, page.PageSize), x => x);
        });
        api.MapGet("cash-bank/contras/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("cashbank.view");
            return Api.Ok(a.Read(tx => ContraJson(tx, CashBook.GetContra(tx, id))));
        });
        api.MapPost("cash-bank/contras", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var c = a.Write((tx, actor) => CashBook.CreateContra(tx, actor, b.ReqDate("date"), b.ReqLong("from_account"), b.ReqLong("to_account"),
                b.ReqDec("amount"), b.Str("reference_number") ?? "", b.Str("notes") ?? ""));
            return Api.Created(a.Read(tx => ContraJson(tx, c)), $"Contra {c.Number} recorded successfully.");
        });
        api.MapPost("cash-bank/contras/{id:long}/cancel", async (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var c = a.Write((tx, actor) => CashBook.CancelContra(tx, actor, id, b.ReqStr("reason")));
            return Api.Ok(a.Read(tx => ContraJson(tx, c)), $"Contra {c.Number} cancelled.");
        });

        // ------------------------------------------------------------------ day book
        api.MapGet("reports/day-book", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("reports.view");
            var start = a.Date("start_date") ?? a.Db.Today();
            var end = a.Date("end_date") ?? start;
            var book = a.Read(tx => CashBook.DayBook(tx, start, end, a.Q("status") == "ALL"));
            if (a.Q("export") is { } kind)
                return ReportsApi.Export(a, kind, ReportsApi.DayBookTable(a, book));
            return Api.Ok(new
            {
                start_date = Json.D(book.Start), end_date = Json.D(book.End), count = book.Entries.Count,
                totals = book.Totals.Select(t => new { voucher_type = t.VoucherType, amount = Json.M(t.Amount) }).ToList(),
                entries = book.Entries.Select(e => new
                {
                    date = Json.D(e.Date), voucher_type = e.VoucherType, number = e.Number, object_id = e.ObjectId, link = e.Link,
                    particulars = e.Particulars, account_name = e.AccountName, amount = Json.M(e.Amount), status = e.Status,
                }).ToList(),
            });
        });
    }
}
