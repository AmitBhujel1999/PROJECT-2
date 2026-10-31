using System.Globalization;
using System.Text;
using System.Text.Json;
using Accounting.Core;
using Accounting.Core.Parties;
using Accounting.Core.Products;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Accounting.Server.Endpoints;

/// <summary>
/// "Bulk update" screens for items, customers and vendors: save many grid rows at once
/// (POST .../bulk), download the Excel format (GET .../bulk/template) and read an uploaded
/// Excel/CSV file back into rows (POST .../bulk/upload, nothing saved). Same rules as Django's
/// common/bulk.py and common/sheets.py: when any row is wrong nothing is saved.
/// </summary>
public static class BulkApi
{
    private const int MaxRows = 1000;
    private const long MaxBytes = 5 * 1024 * 1024;
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private sealed record SheetColumn(string Key, string Label, string Note, bool Required = false, string[]? Choices = null, int Width = 16,
        string[]? Aliases = null);

    private sealed record SheetFormat(string Title, SheetColumn[] Columns, object[][] Examples, string MatchNote);

    private static readonly string[] YesNo = ["Yes", "No"];
    /// <summary>Same labels as the bulk grid's discount dropdown, so an uploaded sheet maps straight onto it.</summary>
    private static readonly string[] DiscountChoices = ["None", "Percent", "Per unit"];

    private static SheetFormat ProductsFormat() => new("Items",
    [
        new("name", "Item name", "Name of the item.", Required: true, Width: 30, Aliases: ["name", "item", "product"]),
        new("sku_code", "Item code", "Short code, different for every item (e.g. RICE-25). Used to find items already in the app. Leave empty to get one automatically.",
            Width: 16, Aliases: ["sku", "code", "item code", "sku code"]),
        new("hs_code", "HS code", "Customs code printed on tax invoices (e.g. 1006.30). May be left empty.", Width: 12, Aliases: ["hs", "hs code"]),
        new("unit", "Unit", "Pick from the list.", Choices: Enum.GetValues<Unit>().Select(u => u.Display()).ToArray(), Width: 10),
        new("purchase_price", "Buying price", "Price you pay, without VAT.", Width: 14, Aliases: ["cost", "cost price", "purchase price"]),
        new("selling_price", "Selling price", "Price you sell at, without VAT.", Width: 14, Aliases: ["sale price", "price", "rate"]),
        new("tax_rate", "VAT %", "13 for VAT items, 0 for non-VAT items.", Width: 8, Aliases: ["vat", "tax", "tax %", "tax rate"]),
        new("reorder_level", "Low stock alert", "Warn when stock falls to this quantity.", Width: 16, Aliases: ["reorder level", "reorder"]),
        new("sale_discount_type", "Sale discount", "Discount filled in on new sales. Percent, or Per unit (rupees off each unit). Leave empty for None.",
            Choices: DiscountChoices, Width: 14, Aliases: ["sale discount type", "discount"]),
        new("sale_discount_value", "Sale discount value", "The percent, or the rupees off each unit.", Width: 14,
            Aliases: ["sale disc", "sale disc value", "discount value"]),
        new("purchase_discount_type", "Purchase discount", "Discount filled in on new purchase bills. Same choices as the sale discount.",
            Choices: DiscountChoices, Width: 14, Aliases: ["purchase discount type", "buying discount"]),
        new("purchase_discount_value", "Purchase discount value", "The percent, or the rupees off each unit.", Width: 14,
            Aliases: ["purchase disc", "purchase disc value", "buying discount value"]),
        new("is_active", "In use", "Yes or No. Leave empty for Yes.", Choices: YesNo, Width: 8, Aliases: ["active"]),
    ],
    [
        ["Basmati Rice 25kg", "RICE-25", "1006.30", "Kg", 1800, 2100, 13, 10, "Percent", 5, "None", 0, "Yes"],
        ["Sunflower Oil 1L", "OIL-1L", "1512.11", "Ltr", 260, 330, 13, 40, "None", 0, "Per unit", 10, "Yes"],
    ],
    "If an item code is already in the app, that item is changed. Otherwise a new item is added.");

    private static SheetFormat PartiesFormat(PartyType type)
    {
        var customer = type == PartyType.Customer;
        var columns = new List<SheetColumn>
        {
            new("name", "Name", "Full name of the business or person.", Required: true, Width: 30, Aliases: ["customer", "vendor", "party", "party name"]),
            new("phone", "Phone", "Mobile or landline.", Width: 14, Aliases: ["mobile", "phone no", "contact"]),
            new("email", "Email", "Email address.", Width: 24),
            new("pan_vat_no", "PAN/VAT No.", "PAN or VAT number.", Width: 14, Aliases: ["pan", "vat no", "pan no", "pan/vat"]),
            new("address", "Address", "Address.", Width: 24),
            new("credit_terms", "Pays within", "Pick from the list. Choose Custom to type your own days.",
                Choices: Enum.GetValues<CreditTerms>().Select(t => t.Display()).ToArray(), Width: 12, Aliases: ["credit terms", "terms"]),
            new("credit_days", "Days (if Custom)", "Only used when 'Pays within' is Custom.", Width: 12, Aliases: ["days", "credit days"]),
        };
        if (customer)
            columns.Add(new("credit_limit", "Max credit (0 = no limit)", "Largest amount the customer may owe. 0 = no limit.", Width: 18,
                Aliases: ["credit limit", "max credit", "limit"]));
        columns.Add(new("is_active", "In use", "Yes or No. Leave empty for Yes.", Choices: YesNo, Width: 8, Aliases: ["active"]));
        object[][] examples =
        [
            ["Sharma Kirana Pasal", "9800000001", "", "601234567", "Kathmandu", "30 Days", "", .. (customer ? new object[] { 50000 } : []), "Yes"],
            ["Gurung Store", "9800000002", "gurung@example.com", "", "Pokhara", "Cash", "", .. (customer ? new object[] { 0 } : []), "Yes"],
        ];
        return new(customer ? "Customers" : "Vendors", columns.ToArray(), examples,
            "If a name is already in the app, that one is changed. Otherwise a new one is added.");
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("products/bulk", async (HttpContext h) =>
        {
            var a = Api.Of(h);
            a.Require("products.manage");
            var rows = Rows(await a.BodyAsync());
            var result = a.Write((tx, actor) => Save(rows, (row, i, seen) =>
            {
                var current = row.Long("id") is { } id ? ProductService.Find(tx, id) ?? throw new RowGone() : null;
                var input = ProductsApi.Input(row, current);
                var sku = input.SkuCode.Trim().ToUpperInvariant();
                if (sku.Length > 0 && !seen.Add(sku))
                    throw new ValidationError("sku_code", "Used twice in this list.");
                return Apply(current is null, row.Bool("is_active"), current?.IsActive,
                    () => ProductService.Create(tx, actor, input).Id,
                    () => input != ProductsApi.Input(Empty, current!) && ProductService.Update(tx, actor, current!.Id, input) is not null,
                    (pid, active) => ProductService.SetActive(tx, actor, pid, active), current?.Id);
            }));
            return Api.Ok(result, Summary(result, "item"));
        });
        api.MapGet("products/bulk/template", (HttpContext h) =>
        {
            Api.Of(h).Require("products.view");
            return Template(ProductsFormat());
        });
        api.MapPost("products/bulk/upload", async (HttpContext h) =>
        {
            Api.Of(h).Require("products.manage");
            return await Upload(h, ProductsFormat());
        });

        foreach (var (route, type) in new[] { ("customers", PartyType.Customer), ("vendors", PartyType.Vendor) })
        {
            var noun = type == PartyType.Customer ? "customer" : "vendor";
            api.MapPost($"{route}/bulk", async (HttpContext h) =>
            {
                var a = Api.Of(h);
                a.Require("parties.manage");
                var rows = Rows(await a.BodyAsync());
                var result = a.Write((tx, actor) => Save(rows, (row, i, _) =>
                {
                    Party? current = null;
                    if (row.Long("id") is { } id)
                    {
                        try { current = PartyService.Get(tx, id, type); }
                        catch (NotFoundError) { throw new RowGone(); }
                    }
                    var input = PartiesApi.Input(row, current);
                    return Apply(current is null, row.Bool("is_active"), current?.IsActive,
                        () => PartyService.Create(tx, actor, type, input).Id,
                        () => input != PartiesApi.Input(Empty, current!) && PartyService.Update(tx, actor, current!.Id, input) is not null,
                        (pid, active) => PartyService.SetActive(tx, actor, pid, active), current?.Id);
                }));
                return Api.Ok(result, Summary(result, noun));
            });
            api.MapGet($"{route}/bulk/template", (HttpContext h) =>
            {
                Api.Of(h).Require("parties.view");
                return Template(PartiesFormat(type));
            });
            api.MapPost($"{route}/bulk/upload", async (HttpContext h) =>
            {
                Api.Of(h).Require("parties.manage");
                return await Upload(h, PartiesFormat(type));
            });
        }
    }

    // ------------------------------------------------------------------ saving grid rows

    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    /// <summary>A grid row points at a record that no longer exists.</summary>
    private sealed class RowGone : Exception;

    private sealed record Outcome(bool Created, bool Updated);

    private sealed record Saved(int created, int updated);

    private static List<JsonElement> Rows(JsonElement body)
    {
        var rows = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().ToList()
            : [];
        if (rows.Count == 0)
            throw new BusinessError("Nothing to save.", "EMPTY");
        if (rows.Count > MaxRows)
            throw new BusinessError($"Save at most {MaxRows} rows at a time.", "TOO_MANY_ROWS");
        return rows;
    }

    /// <summary>
    /// Saves every row inside the caller's transaction. Rows that fail are collected; when any did,
    /// BULK_INVALID is thrown with the problems per row, which rolls back the rows already saved.
    /// </summary>
    private static Saved Save(List<JsonElement> rows, Func<JsonElement, int, HashSet<string>, Outcome> saveRow)
    {
        var errors = new Dictionary<string, object?>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].ValueKind != JsonValueKind.Object)
            {
                errors[i.ToString()] = new Dictionary<string, string> { ["row"] = "This row could not be read." };
                continue;
            }
            try
            {
                var outcome = saveRow(rows[i], i, seen);
                created += outcome.Created ? 1 : 0;
                updated += outcome.Updated ? 1 : 0;
            }
            catch (RowGone)
            {
                errors[i.ToString()] = new Dictionary<string, string> { ["row"] = "This one was deleted by someone else. Reload the page." };
            }
            catch (ValidationError e)
            {
                errors[i.ToString()] = new Dictionary<string, string> { [e.Field] = e.Message };
            }
            catch (BusinessError e) when (e is not PermissionDeniedError)
            {
                errors[i.ToString()] = new Dictionary<string, string> { ["row"] = e.Message };
            }
        }
        if (errors.Count > 0)
            throw new BusinessError("Some rows have problems. Nothing was saved.", "BULK_INVALID", 400,
                new Dictionary<string, object?> { ["rows"] = errors });
        return new Saved(created, updated);
    }

    private static Outcome Apply(bool isNew, bool? wantActive, bool? isActive, Func<long> create, Func<bool> update,
        Action<long, bool> setActive, long? id)
    {
        if (isNew)
        {
            var newId = create();
            if (wantActive == false)
                setActive(newId, false);
            return new(true, false);
        }
        var changed = update();
        if (wantActive is { } active && active != isActive)
        {
            setActive(id!.Value, active);
            changed = true;
        }
        return new(false, changed);
    }

    private static string Summary(Saved result, string noun)
    {
        var (created, updated) = result;
        var parts = new List<string>();
        if (created > 0)
            parts.Add($"{created} new {noun}{(created == 1 ? "" : "s")} added");
        if (updated > 0)
            parts.Add($"{updated} {noun}{(updated == 1 ? "" : "s")} changed");
        return parts.Count > 0 ? "Saved: " + string.Join(", ", parts) + "." : "Nothing changed.";
    }

    // ------------------------------------------------------------------ format file

    private static IResult Template(SheetFormat fmt)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(fmt.Title);
        for (var i = 0; i < fmt.Columns.Length; i++)
        {
            var col = fmt.Columns[i];
            var cell = ws.Cell(1, i + 1);
            cell.Value = col.Label;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(col.Required ? "#B91C1C" : "#1E3A8A");
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.CreateComment().AddText(col.Note + (col.Required ? " Must be filled in." : ""));
            ws.Column(i + 1).Width = col.Width;
            if (col.Choices is { } choices)
            {
                var dv = ws.Range(2, i + 1, MaxRows + 1, i + 1).CreateDataValidation();
                dv.List("\"" + string.Join(",", choices) + "\"", true);
                dv.IgnoreBlanks = true;
                dv.ErrorTitle = col.Label;
                dv.ErrorMessage = "Pick one from the list.";
            }
        }
        for (var r = 0; r < fmt.Examples.Length; r++)
            for (var c = 0; c < fmt.Examples[r].Length; c++)
            {
                var cell = ws.Cell(r + 2, c + 1);
                cell.Value = fmt.Examples[r][c] switch { int n => n, string { Length: > 0 } s => s, _ => Blank.Value };
                cell.Style.Font.Italic = true;
                cell.Style.Font.FontColor = XLColor.FromHtml("#64748B");
            }
        ws.SheetView.FreezeRows(1);
        ws.Row(1).Height = 22;

        var help = wb.Worksheets.Add("How to fill");
        var lines = new (string Text, bool Bold)[]
        {
            ($"How to fill the {fmt.Title} sheet", true),
            ("", false),
            ($"1. Fill one row for each {fmt.Title.ToLowerInvariant().TrimEnd('s')}. Start on row 2.", false),
            ("2. Delete the two grey example rows (or type over them).", false),
            ("3. Red headings must be filled in. Blue ones can be left empty.", false),
            ("4. Do not change the headings in row 1.", false),
            ("5. Save the file, then use 'Upload Excel' on the 'Bulk update' screen.", false),
            ("6. Check the rows on screen, then press Save. Nothing is saved before that.", false),
            ("", false),
            (fmt.MatchNote, false),
            ("", false),
            ("Columns", true),
        };
        var row = 0;
        foreach (var (text, bold) in lines)
        {
            row++;
            help.Cell(row, 1).Value = text;
            if (bold)
            {
                help.Cell(row, 1).Style.Font.Bold = true;
                help.Cell(row, 1).Style.Font.FontSize = row == 1 ? 12 : 11;
            }
        }
        foreach (var col in fmt.Columns)
        {
            row++;
            help.Cell(row, 1).Value = col.Label + (col.Required ? " (must fill)" : "");
            help.Cell(row, 1).Style.Font.Bold = true;
            help.Cell(row, 2).Value = col.Note + (col.Choices is { } ch ? $" Choices: {string.Join(", ", ch)}." : "");
        }
        help.Column(1).Width = 28;
        help.Column(2).Width = 90;

        using var buffer = new MemoryStream();
        wb.SaveAs(buffer);
        return Api.File(buffer.ToArray(), XlsxType, $"{fmt.Title.ToLowerInvariant()}-format.xlsx");
    }

    // ------------------------------------------------------------------ upload

    private static async Task<IResult> Upload(HttpContext h, SheetFormat fmt)
    {
        var file = h.Request.HasFormContentType ? (await h.Request.ReadFormAsync()).Files.GetFile("file") : null;
        if (file is null)
            throw new BusinessError("Choose a file to upload.", "NO_FILE");
        var (data, count) = Parse(fmt, ReadRows(file));
        return Api.Ok(data, $"Read {count} row(s) from the file.");
    }

    private static List<List<string>> ReadRows(IFormFile file)
    {
        var name = (file.FileName ?? "").ToLowerInvariant();
        if (file.Length > MaxBytes)
            throw new BusinessError("The file is too big. Keep it under 5 MB.", "FILE_TOO_BIG");
        using var buffer = new MemoryStream();
        file.CopyTo(buffer);
        buffer.Position = 0;
        if (name.EndsWith(".csv"))
            return Csv(new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd());
        if (name.EndsWith(".xls"))
            throw new BusinessError("This is an old Excel file (.xls). In Excel use File > Save As > Excel Workbook (.xlsx), then upload that.",
                "OLD_EXCEL");
        XLWorkbook wb;
        try
        {
            wb = new XLWorkbook(buffer);
        }
        catch (Exception e)
        {
            throw new BusinessError("Could not open this file. Upload an Excel (.xlsx) or CSV file.", "BAD_FILE", 400,
                new Dictionary<string, object?> { ["reason"] = e.GetType().Name });
        }
        using (wb)
        {
            var ws = wb.Worksheets.First();
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            var rows = new List<List<string>>();
            for (var r = 1; r <= lastRow; r++)
            {
                var values = new List<string>();
                for (var c = 1; c <= lastCol; c++)
                    values.Add(Text(ws.Cell(r, c).Value));
                rows.Add(values);
            }
            return rows;
        }
    }

    /// <summary>A cell as plain text: whole numbers without ".0", Yes/No for booleans, dates as yyyy-MM-dd.</summary>
    private static string Text(XLCellValue v)
    {
        if (v.IsBlank || v.IsError)
            return "";
        if (v.IsBoolean)
            return v.GetBoolean() ? "Yes" : "No";
        if (v.IsNumber)
            return ((decimal)v.GetNumber()).ToString("0.#############", CultureInfo.InvariantCulture);
        if (v.IsDateTime)
            return v.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return v.ToString(CultureInfo.InvariantCulture).Trim();
    }

    /// <summary>Comma-separated rows with quoted fields ("a ""b""", c).</summary>
    private static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(field.ToString().Trim()); field.Clear(); }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString().Trim());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().Trim());
            rows.Add(row);
        }
        return rows;
    }

    private static string Norm(string v) => string.Join(' ', v.ToLowerInvariant().Replace("*", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static (object Data, int Count) Parse(SheetFormat fmt, List<List<string>> raw)
    {
        var lookup = new Dictionary<string, string>();
        foreach (var col in fmt.Columns)
            foreach (var name in new[] { col.Label, col.Key }.Concat(col.Aliases ?? []))
                lookup[Norm(name)] = col.Key;

        // The heading row is the first row (of the first 10) that names a known column.
        int? headerAt = null;
        var mapping = new Dictionary<int, string>();
        for (var i = 0; i < Math.Min(raw.Count, 10); i++)
        {
            mapping = raw[i].Select((v, j) => (j, key: lookup.GetValueOrDefault(Norm(v)))).Where(x => x.key is not null)
                .ToDictionary(x => x.j, x => x.key!);
            if (mapping.Count > 0)
            {
                headerAt = i;
                break;
            }
        }
        if (headerAt is null)
            throw new BusinessError(
                $"Could not find the headings. Download the format and keep row 1 as it is (e.g. '{fmt.Columns[0].Label}').", "NO_HEADINGS");
        if (fmt.Columns.FirstOrDefault(c => c.Required && !mapping.ContainsValue(c.Key)) is { } missing)
            throw new BusinessError($"The file has no '{missing.Label}' column. Download the format and use its headings.", "MISSING_COLUMN");

        var examples = fmt.Examples.Select(ex => string.Join("\u0001", ex.Take(2).Select(v => Norm(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""))))
            .ToHashSet();
        var rows = new List<Dictionary<string, string>>();
        var skipped = 0;
        foreach (var line in raw.Skip(headerAt.Value + 1))
        {
            var values = new Dictionary<string, string>();
            foreach (var (j, key) in mapping)
                if (j < line.Count)
                    values[key] = line[j];
            if (values.Values.All(v => v.Length == 0))
                continue;
            var firstTwo = string.Join("\u0001", fmt.Columns.Take(2).Select(c => Norm(values.GetValueOrDefault(c.Key, ""))));
            if (examples.Contains(firstTwo))
            {
                skipped++;
                continue;
            }
            rows.Add(values);
        }
        if (rows.Count == 0)
            throw new BusinessError("The file has no rows to load.", "EMPTY_FILE");
        if (rows.Count > MaxRows)
            throw new BusinessError($"The file has {rows.Count} rows. Upload at most {MaxRows} at a time.", "TOO_MANY_ROWS");
        var labels = fmt.Columns.ToDictionary(c => c.Key, c => c.Label);
        return (new { rows, columns = mapping.Values.Distinct().Select(k => labels[k]).ToList(), skipped_examples = skipped }, rows.Count);
    }
}
