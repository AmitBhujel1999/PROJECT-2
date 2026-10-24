using Accounting.Core.Common;
using Accounting.Core.Data;
using Accounting.Core.Expenses;
using Accounting.Core.Journal;
using Accounting.Core.Parties;
using Accounting.Core.Payments;
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

    /// <summary>Label / amount pairs aligned to the right edge (totals box).</summary>
    private static void Totals(Section section, IEnumerable<(string Label, string Value, bool Bold)> rows)
    {
        var width = Pdf.ContentWidth(section);
        var table = section.AddTable();
        table.AddColumn(Unit.FromPoint(width * 0.55));
        table.AddColumn(Unit.FromPoint(width * 0.27));
        table.AddColumn(Unit.FromPoint(width * 0.18)).Format.Alignment = ParagraphAlignment.Right;
        foreach (var (label, value, bold) in rows)
        {
            var row = table.AddRow();
            row.Format.Font.Bold = bold;
            if (bold)
            {
                row.Borders.Top.Width = row.Borders.Bottom.Width = 0.8;
                row.Cells[0].Borders.Top.Visible = row.Cells[0].Borders.Bottom.Visible = false;
            }
            row.Cells[1].AddParagraph(label);
            row.Cells[2].AddParagraph(value);
        }
    }

    // ------------------------------------------------------------------ invoice / bill

    public static byte[] TradeDocument(Tx tx, TradeKind kind, long id)
    {
        var s = BusinessSettings.Get(tx);
        var doc = TradeService.Get(tx, kind, id);
        var party = PartyService.Get(tx, doc.PartyId);
        var title = (kind.IsSale ? "TAX INVOICE" : "PURCHASE BILL") + (doc.IsCancelled ? " (CANCELLED)" : "");
        var meta = new List<(string, string)>
        {
            (kind.IsSale ? "Invoice No." : "Bill No.", doc.Number),
            ("Date", D(s, doc.Date)),
            ("Due Date", D(s, doc.DueDate)),
            ("Payment Status", Format.Status(doc.PaymentStatus)),
        };
        if (!kind.IsSale && doc.VendorBillNumber.Length > 0)
            meta.Insert(1, ("Vendor Bill No.", doc.VendorBillNumber));
        var pdf = Pdf.NewDocument($"{title} {doc.Number}");
        var section = pdf.LastSection!;
        Pdf.Header(section, s, title, meta);
        PartyBlock(section, kind.IsSale ? "Bill To" : "Vendor", party);

        ReportColumn[] columns =
        [
            new("#", Width: 0.4), new("Item", Width: 3), new("Qty", true, 1), new("Unit Price", true, 1.1), new("Gross", true, 1.1),
            new("Discount", true, 1), new("Taxable", true, 1.1), new($"{s.TaxLabel} %", true, 0.7), new(s.TaxLabel, true, 1), new("Total", true, 1.2),
        ];
        var grid = Pdf.GridTable(section, columns, Pdf.ContentWidth(section));
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            var l = doc.Lines[i];
            var row = Pdf.AddRow(grid, [(i + 1).ToString(), l.ProductName, $"{Format.Qty(l.Quantity)} {l.Unit}", Format.Money(l.UnitPrice), Format.Money(l.GrossAmount),
                Format.Money(l.DiscountAmount + l.InvoiceDiscountShare), Format.Money(l.TaxableAmount), $"{l.TaxRate:0.00}", Format.Money(l.TaxAmount),
                Format.Money(l.LineTotal)], shaded: i % 2 == 1);
            var sku = row.Cells[1].AddParagraph(l.SkuCode);
            sku.Format.Font.Size = 6.5;
            sku.Format.Font.Color = Pdf.Muted;
        }
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);
        Totals(section,
        [
            ("Subtotal (Gross)", Format.Money(doc.Subtotal), false),
            ("Item Discounts", Format.Money(doc.ItemDiscountTotal), false),
            ("Invoice Discount" + (doc.DiscountType == DiscountType.Percentage ? $" ({doc.DiscountValue:0.00}%)" : ""), Format.Money(doc.DiscountAmount), false),
            ("Taxable Amount", Format.Money(doc.TaxableAmount), false),
            (s.TaxLabel, Format.Money(doc.TaxAmount), false),
            ($"GRAND TOTAL ({s.CurrencyCode})", Format.Money(doc.TotalAmount), true),
            ("Amount Paid", Format.Money(doc.AmountPaid), false),
            ("Balance Due", Format.Money(doc.BalanceDue), false),
        ]);
        Note(section, "Notes", doc.Notes);
        if (doc.IsCancelled)
            Note(section, "Cancelled", doc.CancelReason);
        if (kind.IsSale && s.InvoiceFooter.Length > 0)
        {
            var footer = section.AddParagraph(s.InvoiceFooter);
            footer.Format.SpaceBefore = Unit.FromPoint(14);
            footer.Format.Font.Size = 8;
        }
        Signature(section);
        return Pdf.Render(pdf);
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
