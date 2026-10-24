using Accounting.Core;
using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Expenses;
using Accounting.Core.Reports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>/api/expense-categories/ and /api/expenses/ (expenses are cancelled, never edited or deleted).</summary>
public static class ExpensesApi
{
    private static object CategoryJson(Tx tx, ExpenseCategory c)
    {
        var times = tx.Row("SELECT created_at, updated_at FROM expense_categories WHERE id = @id", new { id = c.Id })!;
        return new
        {
            id = c.Id, name = c.Name, description = c.Description, is_active = c.IsActive, expense_count = c.ExpenseCount,
            created_at = Json.T(times.Timestamp("created_at")), updated_at = Json.T(times.Timestamp("updated_at")),
        };
    }

    public static Dictionary<string, object?> ExpenseJson(Tx tx, Expense e, bool detail)
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = e.Id, ["expense_number"] = e.Number, ["date"] = Json.D(e.Date), ["category"] = e.CategoryId, ["category_name"] = e.CategoryName,
            ["vendor"] = e.VendorId, ["vendor_name"] = e.VendorName, ["payee"] = e.Payee, ["paid_to"] = e.PaidTo, ["description"] = e.Description,
            ["amount"] = Json.M(e.Amount), ["tax_rate"] = Json.R(e.TaxRate), ["tax_amount"] = Json.M(e.TaxAmount), ["total_amount"] = Json.M(e.TotalAmount),
            ["payment_method"] = e.PaymentMethod, ["payment_method_display"] = PaymentMethod.Display(e.PaymentMethod), ["account"] = e.AccountId,
            ["account_name"] = e.AccountName, ["reference_number"] = e.ReferenceNumber, ["status"] = e.Status, ["created_at"] = Json.T(e.CreatedAt),
        };
        if (!detail)
            return data;
        var r = tx.Row(
            """
            SELECT e.cancelled_at, e.updated_at, c.username AS created_by_name, x.username AS cancelled_by_name FROM expenses e
            LEFT JOIN users c ON c.id = e.created_by LEFT JOIN users x ON x.id = e.cancelled_by WHERE e.id = @id
            """, new { id = e.Id })!;
        data["notes"] = e.Notes;
        data["created_by_name"] = r.NullableStr("created_by_name");
        data["cancelled_at"] = r.NullableStr("cancelled_at") is null ? null : Json.T(r.Timestamp("cancelled_at"));
        data["cancelled_by_name"] = r.NullableStr("cancelled_by_name");
        data["cancel_reason"] = e.CancelReason;
        data["updated_at"] = Json.T(r.Timestamp("updated_at"));
        return data;
    }

    public static ExpenseFilter Filter(Api a) => new()
    {
        Start = a.Date("start_date"), End = a.Date("end_date"), CategoryId = a.Int("category"), VendorId = a.Int("vendor"),
        AccountId = a.Int("account"), Search = a.Q("search"), Status = a.Q("status") ?? "ALL",
    };

    public static void Map(RouteGroupBuilder api)
    {
        // ------------------------------------------------------------------ categories
        api.MapGet("expense-categories", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("expenses.view");
            var rows = a.Read(tx => ExpenseService.Categories(tx, a.Bool("is_active"), a.Q("search")).Select(c => CategoryJson(tx, c)).ToList());
            return a.PagedList(rows, x => x);
        });
        api.MapGet("expense-categories/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("expenses.view");
            return Api.Ok(a.Read(tx => CategoryJson(tx, ExpenseService.GetCategory(tx, id))));
        });
        api.MapPost("expense-categories", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var c = a.Write((tx, actor) => ExpenseService.CreateCategory(tx, actor, b.ReqStr("name"), b.Str("description") ?? ""));
            return Api.Created(a.Read(tx => CategoryJson(tx, c)), $"Category {c.Name} created successfully.");
        });
        api.MapMethods("expense-categories/{id:long}", ["PUT", "PATCH"], async (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var current = a.Read(tx => ExpenseService.GetCategory(tx, id));
            var c = a.Write((tx, actor) => ExpenseService.UpdateCategory(tx, actor, id, b.StrOr("name", current.Name), b.StrOr("description", current.Description)));
            return Api.Ok(a.Read(tx => CategoryJson(tx, c)), $"Category {c.Name} updated successfully.");
        });
        api.MapDelete("expense-categories/{id:long}", (HttpContext h, long id) =>
        {
            Api.Of(h).Write((tx, actor) => { ExpenseService.DeleteCategory(tx, actor, id); return 0; });
            return Api.Ok(null, "Category deleted.");
        });
        foreach (var (verb, active) in new[] { ("activate", true), ("deactivate", false) })
            api.MapPost($"expense-categories/{{id:long}}/{verb}", (HttpContext h, long id) =>
            {
                var a = Api.Of(h);
                var c = a.Write((tx, actor) => ExpenseService.SetCategoryActive(tx, actor, id, active));
                return Api.Ok(a.Read(tx => CategoryJson(tx, c)), $"{c.Name} {verb}d.");
            });

        // ------------------------------------------------------------------ expenses
        api.MapGet("expenses", (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("expenses.view");
            var (page, rows) = a.Read(tx =>
            {
                var p = ExpenseService.List(tx, Filter(a), a.Page);
                return (p, p.Results.Select(e => (object)ExpenseJson(tx, e, false)).ToList());
            });
            return Api.Paged(new Page<object>(rows, page.Count, page.PageNumber, page.PageSize), x => x);
        });
        api.MapGet("expenses/{id:long}", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("expenses.view");
            return Api.Ok(a.Read(tx => ExpenseJson(tx, ExpenseService.Get(tx, id), true)));
        });
        api.MapPost("expenses", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var amount = b.ReqDec("amount");
            if (amount < 0.01m)
                throw new ValidationError("amount", "Ensure this value is greater than or equal to 0.01.");
            var input = new ExpenseInput
            {
                Date = b.ReqDate("date"), CategoryId = b.ReqLong("category"), VendorId = b.Long("vendor"), Payee = b.Str("payee") ?? "",
                Description = b.ReqStr("description"), Amount = amount, TaxRate = b.Dec("tax_rate") ?? 0, PaymentMethod = b.Str("payment_method"),
                AccountId = b.Long("account"), ReferenceNumber = b.Str("reference_number") ?? "", Notes = b.Str("notes") ?? "",
            };
            var e = a.Write((tx, actor) => ExpenseService.Create(tx, actor, input));
            return Api.Created(a.Read(tx => ExpenseJson(tx, e, true)), $"Expense {e.Number} recorded successfully.");
        });
        api.MapPost("expenses/calculate", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("expenses.create");
            var b = await a.BodyAsync();
            var amount = b.ReqDec("amount");
            if (amount < 0.01m)
                throw new ValidationError("amount", "Ensure this value is greater than or equal to 0.01.");
            var rate = b.Dec("tax_rate") ?? 0;
            if (rate is < 0 or > 100)
                throw new ValidationError("tax_rate", "Ensure this value is less than or equal to 100.");
            var t = ExpenseService.Calculate(amount, rate);
            return Api.Ok(new { amount = Json.M(t.Amount), tax_rate = Json.R(t.TaxRate), tax_amount = Json.M(t.TaxAmount), total_amount = Json.M(t.TotalAmount) });
        });
        api.MapPost("expenses/{id:long}/cancel", async (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            var b = await a.BodyAsync();
            var e = a.Write((tx, actor) => ExpenseService.Cancel(tx, actor, id, b.ReqStr("reason")));
            return Api.Ok(a.Read(tx => ExpenseJson(tx, e, true)), $"Expense {e.Number} cancelled.");
        });
        api.MapGet("expenses/{id:long}/pdf", (HttpContext h, long id) =>
        {
            var a = Api.Of(h);
            a.Require("expenses.view");
            var (pdf, number) = a.Read(tx => (DocumentPdf.Expense(tx, id), ExpenseService.Get(tx, id).Number));
            return Api.File(pdf, "application/pdf", $"{number}.pdf", inline: a.Q("inline") == "1");
        });
    }
}
