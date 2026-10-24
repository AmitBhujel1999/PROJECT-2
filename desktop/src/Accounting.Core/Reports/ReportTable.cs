using System.Text;
using Accounting.Core.Common;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Accounting.Core.Reports;

/// <summary>A column of an exported report. Width is relative (PDF only).</summary>
public sealed record ReportColumn(string Label, bool Numeric = false, double Width = 1);

/// <summary>
/// Any report as rows of already-formatted text, so one table feeds the CSV export, the PDF export and
/// printing in the same way (the phone API's exports use it too).
/// </summary>
public sealed class ReportTable
{
    public required string Title { get; init; }
    /// <summary>Filters and dates shown under the title, e.g. ("Period", "2083-05-01 to 2083-05-31").</summary>
    public IReadOnlyList<(string Label, string Value)> Meta { get; init; } = [];
    /// <summary>Key figures shown above the table.</summary>
    public IReadOnlyList<(string Label, string Value)> Summary { get; init; } = [];
    public required IReadOnlyList<ReportColumn> Columns { get; init; }
    public List<string[]> Rows { get; init; } = [];
    /// <summary>Bold rows (group headings / subtotals) by index into <see cref="Rows"/>.</summary>
    public HashSet<int> BoldRows { get; init; } = [];
    public string[]? Totals { get; init; }
    public bool Landscape { get; init; }

    public void Add(params string[] cells) => Rows.Add(cells);

    public void AddBold(params string[] cells)
    {
        BoldRows.Add(Rows.Count);
        Rows.Add(cells);
    }

    /// <summary>"report-title-20261023" style file name.</summary>
    public string FileName(string extension, DateOnly today) =>
        new string(Title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-').Replace("--", "-")
        + $"-{today:yyyyMMdd}.{extension}";
}

/// <summary>CSV for Excel: UTF-8 with BOM, quoted, and safe from spreadsheet formula injection.</summary>
public static class Csv
{
    public static byte[] Write(ReportTable table, BusinessSettings settings)
    {
        var sb = new StringBuilder();
        void Line(IEnumerable<string> cells) => sb.Append(string.Join(",", cells.Select(Quote))).Append("\r\n");
        Line([settings.BusinessName]);
        Line([table.Title]);
        foreach (var (label, value) in table.Meta)
            Line([label, value]);
        foreach (var (label, value) in table.Summary)
            Line([label, value]);
        sb.Append("\r\n");
        Line(table.Columns.Select(c => c.Label));
        foreach (var row in table.Rows)
            Line(row);
        if (table.Totals is { } totals)
            Line(totals);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Quote(string value)
    {
        value = Safe(value ?? "");
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>Text that a spreadsheet would run as a formula gets a leading apostrophe; real negative numbers stay.</summary>
    public static string Safe(string value)
    {
        if (value.Length == 0 || "=+-@\t\r".IndexOf(value[0]) < 0)
            return value;
        return decimal.TryParse(value.Replace(",", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? value
            : "'" + value;
    }
}

/// <summary>PDF output (MigraDoc, MIT licensed): report tables and printable documents share one look.</summary>
public static class Pdf
{
    private static readonly Color HeadBackground = Color.FromRgb(0x1F, 0x3A, 0x5F);
    private static readonly Color RowAlt = Color.FromRgb(0xF5, 0xF8, 0xFB);
    private static readonly Color Grid = Color.FromRgb(0xC5, 0xCD, 0xD8);
    internal static readonly Color Muted = Color.FromRgb(0x5B, 0x66, 0x75);

    static Pdf()
    {
        // Use the fonts installed on this Windows PC (Segoe UI / Arial) for PDFs.
        if (OperatingSystem.IsWindows())
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    internal static Document NewDocument(string title, bool landscape = false)
    {
        var doc = new Document();
        doc.Info.Title = title;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 9;
        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = landscape ? Orientation.Landscape : Orientation.Portrait;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromMillimeter(12);
        section.PageSetup.TopMargin = Unit.FromMillimeter(12);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(14);
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = Muted;
        footer.AddText($"Printed {DateTime.Now:yyyy-MM-dd HH:mm}    ·    Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
        return doc;
    }

    /// <summary>Usable page width in points.</summary>
    internal static double ContentWidth(Section section)
    {
        var setup = section.PageSetup;
        var width = setup.Orientation == Orientation.Landscape ? Unit.FromMillimeter(297) : Unit.FromMillimeter(210);
        return width.Point - setup.LeftMargin.Point - setup.RightMargin.Point;
    }

    /// <summary>Company name, address and PAN on the left; document title and details on the right.</summary>
    internal static void Header(Section section, BusinessSettings settings, string title, IEnumerable<(string Label, string Value)> meta)
    {
        var width = ContentWidth(section);
        var table = section.AddTable();
        table.AddColumn(Unit.FromPoint(width * 0.55));
        table.AddColumn(Unit.FromPoint(width * 0.45));
        var row = table.AddRow();
        var left = row.Cells[0];
        var name = left.AddParagraph(settings.BusinessName);
        name.Format.Font.Size = 13;
        name.Format.Font.Bold = true;
        foreach (var line in new[]
                 {
                     settings.Address, settings.PanVatNo.Length > 0 ? "PAN/VAT: " + settings.PanVatNo : "",
                     string.Join(" | ", new[] { settings.Phone, settings.Email }.Where(x => x.Length > 0)),
                 }.Where(l => l.Length > 0))
            left.AddParagraph(line);
        var right = row.Cells[1];
        right.Format.Alignment = ParagraphAlignment.Right;
        var heading = right.AddParagraph(title);
        heading.Format.Font.Size = 14;
        heading.Format.Font.Bold = true;
        heading.Format.Font.Color = HeadBackground;
        foreach (var (label, value) in meta)
        {
            var p = right.AddParagraph();
            p.AddFormattedText(label + ": ", TextFormat.Bold);
            p.AddText(value);
        }
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);
    }

    /// <summary>A grid table with a dark header row, alternate shading and right-aligned numbers.</summary>
    internal static Table GridTable(Section section, IReadOnlyList<ReportColumn> columns, double width)
    {
        var table = section.AddTable();
        table.Borders.Color = Grid;
        table.Borders.Width = 0.25;
        table.TopPadding = table.BottomPadding = 2;
        table.LeftPadding = table.RightPadding = 3;
        var total = columns.Sum(c => c.Width);
        foreach (var c in columns)
        {
            var column = table.AddColumn(Unit.FromPoint(width * c.Width / total));
            column.Format.Alignment = c.Numeric ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        }
        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = HeadBackground;
        head.Format.Font.Bold = true;
        head.Format.Font.Color = Colors.White;
        for (var i = 0; i < columns.Count; i++)
            head.Cells[i].AddParagraph(columns[i].Label);
        return table;
    }

    internal static Row AddRow(Table table, IReadOnlyList<string> cells, bool bold = false, bool shaded = false)
    {
        var row = table.AddRow();
        if (shaded)
            row.Shading.Color = RowAlt;
        row.Format.Font.Bold = bold;
        for (var i = 0; i < cells.Count && i < table.Columns.Count; i++)
            row.Cells[i].AddParagraph(cells[i] ?? "");
        return row;
    }

    internal static byte[] Render(Document doc)
    {
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, false);
        return stream.ToArray();
    }

    /// <summary>Any report table as a PDF with the company header, filters, key figures and totals.</summary>
    public static byte[] Write(ReportTable table, BusinessSettings settings)
    {
        var doc = NewDocument(table.Title, table.Landscape || table.Columns.Count > 8);
        var section = doc.LastSection!;
        Header(section, settings, table.Title, table.Meta);
        if (table.Summary.Count > 0)
        {
            var p = section.AddParagraph();
            p.Format.SpaceAfter = Unit.FromPoint(6);
            for (var i = 0; i < table.Summary.Count; i++)
            {
                if (i > 0)
                    p.AddText("     ");
                p.AddFormattedText(table.Summary[i].Label + ": ", TextFormat.Bold);
                p.AddText(table.Summary[i].Value);
            }
        }
        var grid = GridTable(section, table.Columns, ContentWidth(section));
        for (var i = 0; i < table.Rows.Count; i++)
            AddRow(grid, table.Rows[i], table.BoldRows.Contains(i), shaded: i % 2 == 1);
        if (table.Totals is { } totals)
        {
            var row = AddRow(grid, totals, bold: true);
            row.Borders.Top.Width = 0.8;
            row.Borders.Top.Color = Colors.Black;
        }
        if (table.Rows.Count == 0)
            section.AddParagraph("No records.").Format.Font.Color = Muted;
        return Render(doc);
    }
}
