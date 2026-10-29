using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Expenses;
using Accounting.Core.Journal;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
using Accounting.Core.Security;
using Accounting.Core.Trade;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace Accounting.Core.Reports;

/// <summary>Printable invoices, bills, receipts, payment/expense/journal vouchers and party statements (PDF).</summary>
public static class DocumentPdf
{
    private static string D(BusinessSettings s, DateOnly d) => Format.Date(d, s.DateSystem == "BS");

    private static void PartyBlock(Section section, string title, Party party)
    {
        var p = section.AddParagraph();
        p.AddFormattedText(title + "\n", TextFormat.Bold);
        p.AddFormattedText(party.Name + "\n", TextFormat.Bold);
        foreach (var line in new[] { party.Address, party.PanVatNo.Length > 0 ? "PAN/VAT: " + party.PanVatNo : "", party.Phone.Length > 0 ? "Phone: " + party.Phone : "" }
                     .Where(l => l.Length > 0))
            p.AddText(line + "\n");
        p.Format.SpaceAfter = Unit.FromPoint(8);
    }

    private static void Note(Section section, string label, string text)
    {
        if (text.Length == 0)
            return;
        var p = section.AddParagraph();
        p.Format.SpaceBefore = Unit.FromPoint(6);
        p.AddFormattedText(label + ": ", TextFormat.Bold);
        p.AddText(text);
    }

    private static void Signature(Section section)
    {
        var p = section.AddParagraph("\n\n_______________________\nAuthorised Signature");
        p.Format.SpaceBefore = Unit.FromPoint(18);
        p.Format.Font.Size = 8;
    }

    // ------------------------------------------------------------------ invoice / bill

    /// <summary>
    /// Sales invoice in the IRD tax invoice format (VAT Rules 2053, rule 17, schedule 5), or a purchase bill in the same layout.
    /// With <paramref name="printedBy"/>, a sales invoice print is recorded: the first is the original, later ones say "Copy of original (n)".
    /// A <paramref name="preview"/> is never recorded and is marked "Preview", so it cannot pass for an issued invoice.
    /// </summary>
    public static byte[] TradeDocument(Tx tx, TradeKind kind, long id, Actor? printedBy = null, bool preview = false)
    {
        var s = BusinessSettings.Get(tx);
        var doc = TradeService.Get(tx, kind, id);
        var party = PartyService.Get(tx, doc.PartyId);
        var sale = kind.IsSale;
        var copyLabel = "";
        if (sale)
        {
            var printed = tx.Scalar<long>("SELECT COUNT(*) FROM invoice_prints WHERE sale_id = @id", new { id });
            if (printedBy is not null && !preview)
                tx.Execute("INSERT INTO invoice_prints (sale_id, printed_by) VALUES (@id, @userId)", new { id, userId = printedBy.UserId });
            copyLabel = preview ? "PREVIEW – NOT AN INVOICE" : printed == 0 ? "ORIGINAL – FOR BUYER" : $"COPY OF ORIGINAL ({printed})";
        }
        var title = sale ? "TAX INVOICE" : "PURCHASE BILL";
        var pdf = Pdf.NewDocument($"{title} {doc.Number}");
        var section = pdf.LastSection!;
        var width = Pdf.ContentWidth(section);
        PrintFooter(section, printedBy);

        // copy label (and cancelled stamp), top right
        foreach (var label in new[] { doc.IsCancelled ? "CANCELLED" : "", copyLabel }.Where(l => l.Length > 0))
        {
            var box = BoxedText(section, label, 8, doc.IsCancelled && label == "CANCELLED" ? Colors.DarkRed : Colors.Black);
            box.Rows.Alignment = RowAlignment.Right;
            section.AddParagraph().Format.Font.Size = 2;
        }

        // seller
        var seller = section.AddParagraph();
        seller.Format.Alignment = ParagraphAlignment.Center;
        seller.AddFormattedText(s.BusinessName.ToUpperInvariant() + "\n", new Font { Size = 16, Bold = true });
        var contact = string.Join("  |  ", new[] { s.Address, s.Phone.Length > 0 ? "Phone: " + s.Phone : "", s.Email }.Where(x => x.Length > 0));
        if (contact.Length > 0)
            seller.AddText(contact + "\n");
        seller.AddFormattedText("PAN / VAT No.: " + (s.PanVatNo.Length > 0 ? s.PanVatNo : "________"), TextFormat.Bold);
        seller.Format.SpaceAfter = Unit.FromPoint(8);

        BoxedText(section, title, 13, Colors.Black, padding: 4).Rows.Alignment = RowAlignment.Center;
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);

        // buyer / document details
        var info = section.AddTable();
        info.Borders.Width = 0.75;
        info.LeftPadding = info.RightPadding = 6;
        info.TopPadding = info.BottomPadding = 4;
        info.AddColumn(Unit.FromPoint(width * 0.58));
        info.AddColumn(Unit.FromPoint(width * 0.42));
        var infoRow = info.AddRow();
        var who = sale ? "Buyer's" : "Vendor's";
        InfoLines(infoRow.Cells[0], 108,
        [
            ($"{who} Name:", party.Name),
            ("Address:", party.Address),
            ($"{who} PAN/VAT No.:", party.PanVatNo),
        ]);
        var pay = infoRow.Cells[0].AddParagraph();
        pay.Format.TabStops.AddTabStop(Unit.FromPoint(108));
        pay.AddFormattedText("Method of Payment:", TextFormat.Bold);
        pay.AddTab();
        var method = PaymentMethodOf(tx, kind, doc);
        foreach (var m in new[] { "Cash", "Cheque", "Credit", "Other" })
        {
            pay.AddFormattedText(m == method ? "■ " : "□ ", TextFormat.Bold);
            pay.AddText(m + "    ");
        }
        var details = new List<(string, string)> { (sale ? "Invoice No.:" : "Bill No.:", doc.Number) };
        if (!sale && doc.VendorBillNumber.Length > 0)
            details.Add(("Vendor Bill No.:", doc.VendorBillNumber));
        details.Add(("Fiscal Year:", FiscalYear(doc.Date)));
        details.Add(("Transaction Date:", BothDates(doc.Date)));
        details.Add((sale ? "Invoice Issue Date:" : "Entry Date:", BothDates(DateOnly.FromDateTime(doc.CreatedAt.ToLocalTime()))));
        if (doc.BalanceDue > 0 && doc.DueDate != doc.Date)
            details.Add(("Due Date:", BothDates(doc.DueDate)));
        InfoLines(infoRow.Cells[1], 92, details);
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);

        // items
        var items = section.AddTable();
        items.Borders.Width = 0.75;
        items.TopPadding = items.BottomPadding = 3;
        items.LeftPadding = items.RightPadding = 6;
        (string Label, double Width, ParagraphAlignment Align)[] columns =
        [
            ("S.N.", 0.45, ParagraphAlignment.Center), ("HS Code", 0.95, ParagraphAlignment.Center), ("Particulars", 3.45, ParagraphAlignment.Left),
            ("Quantity", 0.85, ParagraphAlignment.Right), ("Unit", 0.6, ParagraphAlignment.Center), ($"Rate ({s.CurrencySymbol})", 1.05, ParagraphAlignment.Right),
            ($"Amount ({s.CurrencySymbol})", 1.25, ParagraphAlignment.Right),
        ];
        var totalWidth = columns.Sum(c => c.Width);
        foreach (var c in columns)
            items.AddColumn(Unit.FromPoint(width * c.Width / totalWidth)).Format.Alignment = c.Align;
        var head = items.AddRow();
        head.HeadingFormat = true;
        head.Format.Font.Bold = true;
        head.Shading.Color = Colors.WhiteSmoke;
        for (var i = 0; i < columns.Length; i++)
        {
            head.Cells[i].AddParagraph(columns[i].Label);
            head.Cells[i].Format.Alignment = ParagraphAlignment.Center;
        }
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            var l = doc.Lines[i];
            var row = items.AddRow();
            row.Borders.Top.Visible = row.Borders.Bottom.Visible = false;
            string[] cells = [(i + 1).ToString(), l.HsCode, l.ProductName + (l.TaxRate == 0 ? " (VAT exempt)" : ""), Format.Qty(l.Quantity), l.Unit,
                Format.Money(l.UnitPrice), Format.Money(l.GrossAmount)];
            for (var c = 0; c < cells.Length; c++)
                row.Cells[c].AddParagraph(cells[c]);
        }
        var filler = items.AddRow();
        filler.Borders.Top.Visible = false;
        filler.Height = Unit.FromPoint(Math.Max(20, (12 - doc.Lines.Count) * 15));

        // amount in words and totals
        var exempt = doc.Lines.Where(l => l.TaxRate == 0).Sum(l => l.TaxableAmount);
        var rates = doc.Lines.Where(l => l.TaxRate > 0).Select(l => l.TaxRate).Distinct().ToList();
        (string Label, decimal Value)[] totals =
        [
            ("Total", doc.Subtotal),
            ("Discount", doc.TotalDiscount),
            ("Exempted Amount", exempt),
            ("Taxable Amount", doc.TaxableAmount - exempt),
            (s.TaxLabel + (rates.Count == 1 ? $" {rates[0]:0.##}%" : ""), doc.TaxAmount),
            ($"Grand Total ({s.CurrencySymbol})", doc.TotalAmount),
        ];
        var bottom = section.AddTable();
        bottom.Borders.Width = 0.75;
        bottom.TopPadding = bottom.BottomPadding = 3;
        bottom.LeftPadding = bottom.RightPadding = 6;
        bottom.AddColumn(Unit.FromPoint(width * 0.58));
        bottom.AddColumn(Unit.FromPoint(width * 0.24));
        bottom.AddColumn(Unit.FromPoint(width * 0.18)).Format.Alignment = ParagraphAlignment.Right;
        for (var i = 0; i < totals.Length; i++)
        {
            var row = bottom.AddRow();
            row.Cells[1].AddParagraph(totals[i].Label);
            row.Cells[2].AddParagraph(Format.Money(totals[i].Value));
            if (i == totals.Length - 1)
            {
                row.Format.Font.Bold = true;
                row.Format.Font.Size = 10;
                row.Cells[1].Shading.Color = row.Cells[2].Shading.Color = Colors.WhiteSmoke;
            }
        }
        var left = bottom.Rows[0].Cells[0];
        left.MergeDown = totals.Length - 1;
        var words = left.AddParagraph();
        words.AddFormattedText("Amount in words:\n", TextFormat.Bold);
        words.AddText(Format.AmountInWords(doc.TotalAmount) + ".");
        if (doc.AmountPaid > 0 && doc.BalanceDue > 0)
            SmallNote(left, "Paid", $"{Format.Money(doc.AmountPaid)}    Balance due: {Format.Money(doc.BalanceDue)}");
        SmallNote(left, "Remarks", doc.Notes);
        if (doc.IsCancelled)
            SmallNote(left, "Cancelled", doc.CancelReason);
        if (sale && s.InvoiceFooter.Length > 0)
            SmallNote(left, "", s.InvoiceFooter);

        // signatures
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(50);
        var sign = section.AddTable();
        sign.AddColumn(Unit.FromPoint(width * 0.32));
        sign.AddColumn(Unit.FromPoint(width * 0.36));
        sign.AddColumn(Unit.FromPoint(width * 0.32));
        var signRow = sign.AddRow();
        signRow.Format.Alignment = ParagraphAlignment.Center;
        foreach (var (cell, text) in new[] { (signRow.Cells[0], sale ? "Received by (Buyer)" : "Prepared by"), (signRow.Cells[2], $"For {s.BusinessName}\nAuthorised Signature") })
        {
            cell.Borders.Top.Width = 0.75;
            cell.AddParagraph(text);
        }
        return Pdf.Render(pdf);
    }

    /// <summary>One-cell table with a border around a short text (copy label, document title).</summary>
    private static Table BoxedText(Section section, string text, double size, Color color, double padding = 2)
    {
        var table = section.AddTable();
        table.Borders.Width = 1;
        table.Borders.Color = color;
        table.TopPadding = table.BottomPadding = padding;
        table.LeftPadding = table.RightPadding = 10;
        var p = new Paragraph();
        p.AddFormattedText(text, new Font { Size = size, Bold = true, Color = color });
        table.AddColumn(Unit.FromPoint(size * 0.62 * text.Length + 24));
        table.AddRow().Cells[0].Add(p);
        p.Format.Alignment = ParagraphAlignment.Center;
        return table;
    }

    /// <summary>"Label:  value" lines with the values lined up at one tab stop.</summary>
    private static void InfoLines(Cell cell, double tab, IEnumerable<(string Label, string Value)> lines)
    {
        foreach (var (label, value) in lines)
        {
            var p = cell.AddParagraph();
            p.Format.TabStops.AddTabStop(Unit.FromPoint(tab));
            p.Format.SpaceAfter = Unit.FromPoint(1.5);
            p.AddFormattedText(label, TextFormat.Bold);
            p.AddTab();
            p.AddText(value);
        }
    }

    private static void SmallNote(Cell cell, string label, string text)
    {
        if (text.Length == 0)
            return;
        var p = cell.AddParagraph();
        p.Format.SpaceBefore = Unit.FromPoint(6);
        p.Format.Font.Size = 8;
        if (label.Length > 0)
            p.AddFormattedText(label + ": ", TextFormat.Bold);
        p.AddText(text);
    }

    /// <summary>Cash / Cheque / Credit / Other, from the payments allocated to the document. Anything still owed is credit.</summary>
    private static string PaymentMethodOf(Tx tx, TradeKind kind, TradeDocument doc)
    {
        if (doc.AmountPaid == 0 || doc.BalanceDue > 0)
            return "Credit";
        var cfg = AccountConfig.For(kind);
        var methods = PartyAccounts.AllocationsForDocument(tx, cfg, doc.Id).Where(a => a.IsActive)
            .Select(a => PartyAccounts.GetPayment(tx, cfg, a.PaymentId).PaymentMethod).Distinct().ToList();
        return methods switch
        {
            [PaymentMethod.Cash] => "Cash",
            [PaymentMethod.Cheque] => "Cheque",
            _ => "Other",
        };
    }

    /// <summary>"2083-07-12 (2026-10-29)": B.S. first, as the tax office expects, with the A.D. date.</summary>
    private static string BothDates(DateOnly d)
    {
        var bs = Format.Date(d, bs: true);
        return bs.EndsWith(" AD") ? Format.Iso(d) : $"{bs} ({Format.Iso(d)})";
    }

    private static string FiscalYear(DateOnly d)
    {
        try { return NepaliDate.FiscalYear(d).Replace('-', '/'); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    /// <summary>Footer for invoices: who printed it and when (B.S.), and the page number.</summary>
    private static void PrintFooter(Section section, Actor? printedBy)
    {
        section.Footers.Primary.Elements.Clear();
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = Pdf.Muted;
        footer.Format.Borders.Top.Width = 0.5;
        footer.Format.Borders.Top.Color = Colors.LightGray;
        var now = DateTime.Now;
        if (printedBy is { Username.Length: > 0 })
            footer.AddText($"Printed by: {printedBy.Username}    |    ");
        footer.AddText($"Printed on: {Format.Date(DateOnly.FromDateTime(now), bs: true)} {now:HH:mm}    |    Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
        footer.AddText("    |    Mero Lekhapal");
    }

    // ------------------------------------------------------------------ receipt / payment

    public static byte[] Payment(Tx tx, AccountConfig cfg, long id)
    {
        var s = BusinessSettings.Get(tx);
        var payment = PartyAccounts.GetPayment(tx, cfg, id);
        var party = PartyService.Get(tx, payment.PartyId);
        var isReceipt = cfg.Documents.IsSale;
        var title = (isReceipt ? "RECEIPT" : "PAYMENT VOUCHER") + (payment.IsCancelled ? " (CANCELLED)" : "");
        var meta = new List<(string, string)> { ("Number", payment.Number), ("Date", D(s, payment.Date)), (isReceipt ? "Received In" : "Paid From", payment.AccountName) };
        if (payment.ReferenceNumber.Length > 0)
            meta.Add(("Reference", payment.ReferenceNumber));
        var pdf = Pdf.NewDocument($"{title} {payment.Number}");
        var section = pdf.LastSection!;
        Pdf.Header(section, s, title, meta);
        PartyBlock(section, isReceipt ? "Received From" : "Paid To", party);
        var amount = section.AddParagraph();
        amount.AddFormattedText("Amount: ", TextFormat.Bold);
        amount.AddFormattedText($"{s.CurrencyCode} {Format.Money(payment.Amount)}", TextFormat.Bold);
        amount.Format.Font.Size = 11;
        amount.Format.SpaceAfter = Unit.FromPoint(6);
        var grid = Pdf.GridTable(section, [new("Applied To", Width: 2), new("Date"), new("Amount", true)], Pdf.ContentWidth(section));
        foreach (var a in PartyAccounts.AllocationsForPayment(tx, cfg, id).Where(a => a.IsActive))
            Pdf.AddRow(grid, [a.DocumentNumber, D(s, a.Date), Format.Money(a.Amount)]);
        Pdf.AddRow(grid, ["Unallocated / Advance", "", Format.Money(payment.UnallocatedAmount)], bold: true);
        Note(section, "Notes", payment.Notes);
        if (payment.IsCancelled)
            Note(section, "Cancelled", payment.CancelReason);
        Signature(section);
        return Pdf.Render(pdf);
    }

    // ------------------------------------------------------------------ expense voucher

    public static byte[] Expense(Tx tx, long id)
    {
        var s = BusinessSettings.Get(tx);
        var e = ExpenseService.Get(tx, id);
        var title = "EXPENSE VOUCHER" + (e.IsCancelled ? " (CANCELLED)" : "");
        var meta = new List<(string, string)> { ("Number", e.Number), ("Date", D(s, e.Date)), ("Paid From", e.AccountName) };
        if (e.ReferenceNumber.Length > 0)
            meta.Add(("Reference", e.ReferenceNumber));
        var pdf = Pdf.NewDocument($"{title} {e.Number}");
        var section = pdf.LastSection!;
        Pdf.Header(section, s, title, meta);
        if (e.VendorId is { } vendorId)
            PartyBlock(section, "Paid To", PartyService.Get(tx, vendorId));
        else if (e.Payee.Length > 0)
            Note(section, "Paid To", e.Payee);
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
        var grid = Pdf.GridTable(section, [new("Category"), new("Description", Width: 2), new("Amount", true)], Pdf.ContentWidth(section));
        Pdf.AddRow(grid, [e.CategoryName, e.Description, Format.Money(e.Amount)]);
        Pdf.AddRow(grid, ["", $"{s.TaxLabel} @ {e.TaxRate:0.##}%", Format.Money(e.TaxAmount)]);
        Pdf.AddRow(grid, ["", $"Total ({s.CurrencyCode})", Format.Money(e.TotalAmount)], bold: true);
        Note(section, "Notes", e.Notes);
        if (e.IsCancelled)
            Note(section, "Cancelled", e.CancelReason);
        Signature(section);
        return Pdf.Render(pdf);
    }

    // ------------------------------------------------------------------ journal voucher

    public static byte[] Journal(Tx tx, long id)
    {
        var s = BusinessSettings.Get(tx);
        var v = JournalService.GetVoucher(tx, id);
        var title = "JOURNAL VOUCHER" + (v.IsCancelled ? " (CANCELLED)" : "");
        var pdf = Pdf.NewDocument($"{title} {v.Number}");
        var section = pdf.LastSection!;
        Pdf.Header(section, s, title, [("Number", v.Number), ("Date", D(s, v.Date))]);
        var grid = Pdf.GridTable(section, [new("Account", Width: 2.2), new("Narration", Width: 2), new("Debit", true), new("Credit", true)], Pdf.ContentWidth(section));
        foreach (var l in v.Lines)
            Pdf.AddRow(grid, [$"{l.Account.Name} ({l.Account.Group})", l.Narration, l.Debit == 0 ? "" : Format.Money(l.Debit), l.Credit == 0 ? "" : Format.Money(l.Credit)]);
        Pdf.AddRow(grid, ["Total", "", Format.Money(v.TotalAmount), Format.Money(v.TotalAmount)], bold: true);
        Note(section, "Narration", v.Narration);
        if (v.IsCancelled)
            Note(section, "Cancelled", v.CancelReason);
        Signature(section);
        return Pdf.Render(pdf);
    }

    // ------------------------------------------------------------------ party statement

    /// <summary>Customer / vendor statement for a period as a report table (CSV or PDF).</summary>
    public static ReportTable Statement(Tx tx, AccountConfig cfg, long partyId, DateOnly? start, DateOnly? end)
    {
        var s = BusinessSettings.Get(tx);
        var bs = s.DateSystem == "BS";
        var party = PartyService.Get(tx, partyId, cfg.PartyType);
        var ledger = PartyLedgers.Ledger(tx, cfg, partyId, start, end);
        var table = new ReportTable
        {
            Title = $"{cfg.Documents.PartyLabel} Statement: {party.Name}",
            Meta =
            [
                ("Period", $"{(start is { } st ? Format.Date(st, bs) : "Beginning")} to {Format.Date(ledger.End, bs)}"),
                .. party.PanVatNo.Length > 0 ? new[] { ("PAN/VAT", party.PanVatNo) } : [],
            ],
            Summary =
            [
                ("Opening Balance", Format.Money(ledger.OpeningBalance)),
                ("Total Debit", Format.Money(ledger.TotalDebit)),
                ("Total Credit", Format.Money(ledger.TotalCredit)),
                ("Closing Balance", Format.Money(ledger.ClosingBalance)),
            ],
            Columns = [new("Date"), new("Reference", Width: 1.3), new("Type"), new("Description", Width: 2.6), new("Debit", true), new("Credit", true), new("Balance", true)],
        };
        table.AddBold("", "", "", "Opening Balance", "", "", Format.Money(ledger.OpeningBalance));
        foreach (var e in ledger.Entries)
            table.Add(Format.Date(e.Date, bs), e.Reference, e.TransactionType, e.Description, e.Debit == 0 ? "" : Format.Money(e.Debit),
                e.Credit == 0 ? "" : Format.Money(e.Credit), Format.Money(e.Balance));
        table.AddBold("", "", "", "Closing Balance", Format.Money(ledger.TotalDebit), Format.Money(ledger.TotalCredit), Format.Money(ledger.ClosingBalance));
        return table;
    }
}
