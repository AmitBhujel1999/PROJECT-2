"""Printable invoice / bill / receipt PDFs."""

from __future__ import annotations

from decimal import Decimal

from django.utils import timezone
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.platypus import Paragraph, Spacer, Table, TableStyle

from apps.common.models import BusinessSettings, DocumentStatus

from .exporters import CELL, CELL_R, GRID, H1, HEAD_BG, NORMAL, RIGHT, SMALL, _esc, build_pdf


def m(value) -> str:
    return f"{Decimal(value):,.2f}"


def q(value) -> str:
    return f"{Decimal(value).normalize():f}"


def _party_block(title: str, party, extra_lines: list[str]) -> Paragraph:
    lines = [f"<b>{_esc(title)}</b>", f"<b>{_esc(party.name)}</b>"]
    if party.address:
        lines.append(_esc(party.address))
    if party.pan_vat_no:
        lines.append(f"PAN/VAT: {_esc(party.pan_vat_no)}")
    if party.phone:
        lines.append(f"Phone: {_esc(party.phone)}")
    lines += extra_lines
    return Paragraph("<br/>".join(lines), NORMAL)


def _header(settings: BusinessSettings, doc_title: str, meta_rows: list[tuple[str, str]], width: float) -> list:
    company = [f"<b>{_esc(settings.business_name)}</b>"]
    if settings.address:
        company.append(_esc(settings.address))
    if settings.pan_vat_no:
        company.append(f"PAN/VAT: {_esc(settings.pan_vat_no)}")
    contact = " | ".join(x for x in [settings.phone, settings.email] if x)
    if contact:
        company.append(_esc(contact))
    meta = "<br/>".join(f"<b>{_esc(k)}:</b> {_esc(v)}" for k, v in meta_rows)
    t = Table(
        [[Paragraph("<br/>".join(company), NORMAL), [Paragraph(_esc(doc_title), H1), Paragraph(meta, RIGHT)]]],
        colWidths=[width * 0.55, width * 0.45],
    )
    t.setStyle(TableStyle([("VALIGN", (0, 0), (-1, -1), "TOP"), ("ALIGN", (1, 0), (1, 0), "RIGHT")]))
    return [t, Spacer(1, 6)]


def trade_document_pdf(doc, *, kind: str) -> bytes:
    """``kind`` is 'sale' or 'purchase'."""
    settings = BusinessSettings.get_solo()
    width = A4[0] - 24 * mm
    is_sale = kind == "sale"
    number = doc.invoice_number if is_sale else doc.bill_number
    party = doc.customer if is_sale else doc.vendor
    title = ("TAX INVOICE" if is_sale else "PURCHASE BILL") + (" (CANCELLED)" if doc.status == DocumentStatus.CANCELLED else "")
    meta = [
        ("Invoice No." if is_sale else "Bill No.", number),
        ("Date", doc.date.strftime("%d/%m/%Y")),
        ("Due Date", doc.due_date.strftime("%d/%m/%Y")),
        ("Payment Status", doc.get_payment_status_display()),
    ]
    if not is_sale and doc.vendor_bill_number:
        meta.insert(1, ("Vendor Bill No.", doc.vendor_bill_number))
    story = _header(settings, title, meta, width)
    story.append(_party_block("Bill To" if is_sale else "Vendor", party, []))
    story.append(Spacer(1, 8))

    tax = settings.tax_label
    head = ["#", "Item", "Qty", "Unit Price", "Gross", "Discount", "Taxable", f"{tax} %", tax, "Total"]
    rows = [[Paragraph(f'<font color="white"><b>{h}</b></font>', CELL if i < 2 else CELL_R) for i, h in enumerate(head)]]
    for i, item in enumerate(doc.items.select_related("product"), start=1):
        price = item.unit_price if is_sale else item.unit_cost
        total = item.total_price if is_sale else item.total_cost
        disc = item.discount_amount + item.invoice_discount_share
        rows.append(
            [
                Paragraph(str(i), CELL),
                Paragraph(f"{_esc(item.product.name)}<br/><font size=6 color='#64748b'>{_esc(item.product.sku_code)}</font>", CELL),
                Paragraph(f"{q(item.quantity)} {_esc(item.product.get_unit_display())}", CELL_R),
                Paragraph(m(price), CELL_R),
                Paragraph(m(item.gross_amount), CELL_R),
                Paragraph(m(disc), CELL_R),
                Paragraph(m(item.taxable_amount), CELL_R),
                Paragraph(f"{item.tax_rate:.2f}", CELL_R),
                Paragraph(m(item.tax_amount), CELL_R),
                Paragraph(m(total), CELL_R),
            ]
        )
    weights = [0.4, 3, 1, 1.1, 1.1, 1, 1.1, 0.7, 1, 1.2]
    widths = [width * w / sum(weights) for w in weights]
    t = Table(rows, colWidths=widths, repeatRows=1)
    t.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), HEAD_BG),
        ("GRID", (0, 0), (-1, -1), 0.25, GRID),
        ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
    ]))
    story += [t, Spacer(1, 8)]

    totals = [
        ("Subtotal (Gross)", m(doc.subtotal)),
        ("Item Discounts", m(doc.item_discount_total)),
        ("Invoice Discount" + (f" ({doc.discount_value:.2f}%)" if doc.discount_type == "PERCENTAGE" else ""), m(doc.discount_amount)),
        ("Taxable Amount", m(doc.taxable_amount)),
        (tax, m(doc.tax_amount)),
        (f"GRAND TOTAL ({settings.currency_code})", m(doc.total_amount)),
        ("Amount Paid", m(doc.amount_paid)),
        ("Balance Due", m(doc.balance_due)),
    ]
    trows = [[Paragraph(f"<b>{_esc(k)}</b>" if "GRAND" in k else _esc(k), NORMAL), Paragraph(f"<b>{v}</b>" if "GRAND" in k else v, RIGHT)] for k, v in totals]
    tt = Table(trows, colWidths=[width * 0.25, width * 0.2], hAlign="RIGHT")
    tt.setStyle(TableStyle([
        ("LINEABOVE", (0, 5), (-1, 5), 0.8, colors.black),
        ("LINEBELOW", (0, 5), (-1, 5), 0.8, colors.black),
        ("TOPPADDING", (0, 0), (-1, -1), 1),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 1),
    ]))
    story.append(tt)
    if doc.notes:
        story += [Spacer(1, 6), Paragraph(f"<b>Notes:</b> {_esc(doc.notes)}", SMALL)]
    if doc.status == DocumentStatus.CANCELLED:
        story += [Spacer(1, 6), Paragraph(f"<b>Cancelled:</b> {_esc(doc.cancel_reason)}", SMALL)]
    if is_sale and settings.invoice_footer:
        story += [Spacer(1, 16), Paragraph(_esc(settings.invoice_footer), SMALL)]
    story += [Spacer(1, 24), Paragraph("_______________________<br/>Authorised Signature", SMALL)]
    return build_pdf(story, title=f"{title} {number}")


def payment_pdf(payment, *, kind: str) -> bytes:
    """``kind`` is 'receipt' or 'payment'."""
    settings = BusinessSettings.get_solo()
    width = A4[0] - 24 * mm
    is_receipt = kind == "receipt"
    number = payment.receipt_number if is_receipt else payment.payment_number
    party = payment.customer if is_receipt else payment.vendor
    title = ("RECEIPT" if is_receipt else "PAYMENT VOUCHER") + (" (CANCELLED)" if payment.status == DocumentStatus.CANCELLED else "")
    meta = [("Number", number), ("Date", payment.date.strftime("%d/%m/%Y")), ("Method", payment.get_payment_method_display())]
    if payment.reference_number:
        meta.append(("Reference", payment.reference_number))
    story = _header(settings, title, meta, width)
    story.append(_party_block("Received From" if is_receipt else "Paid To", party, []))
    story += [Spacer(1, 8), Paragraph(f"<b>Amount:</b> {settings.currency_code} {m(payment.amount)}", NORMAL), Spacer(1, 6)]
    doc_attr = "sale" if is_receipt else "purchase"
    rows = [[Paragraph('<font color="white"><b>Applied To</b></font>', CELL), Paragraph('<font color="white"><b>Date</b></font>', CELL),
             Paragraph('<font color="white"><b>Amount</b></font>', CELL_R)]]
    for a in payment.allocations.filter(is_active=True).select_related(doc_attr):
        d = getattr(a, doc_attr)
        rows.append([Paragraph(_esc(d.invoice_number if is_receipt else d.bill_number), CELL),
                     Paragraph(a.date.strftime("%d/%m/%Y"), CELL), Paragraph(m(a.amount), CELL_R)])
    rows.append([Paragraph("<b>Unallocated / Advance</b>", CELL), Paragraph("", CELL), Paragraph(m(payment.unallocated_amount), CELL_R)])
    t = Table(rows, colWidths=[width * 0.5, width * 0.25, width * 0.25])
    t.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, 0), HEAD_BG), ("GRID", (0, 0), (-1, -1), 0.25, GRID)]))
    story.append(t)
    if payment.notes:
        story += [Spacer(1, 6), Paragraph(f"<b>Notes:</b> {_esc(payment.notes)}", SMALL)]
    story += [Spacer(1, 24), Paragraph(f"Printed {timezone.localtime():%d/%m/%Y %H:%M}<br/><br/>_______________________<br/>Authorised Signature", SMALL)]
    return build_pdf(story, title=f"{title} {number}")


def expense_pdf(expense) -> bytes:
    settings = BusinessSettings.get_solo()
    width = A4[0] - 24 * mm
    title = "EXPENSE VOUCHER" + (" (CANCELLED)" if expense.status == DocumentStatus.CANCELLED else "")
    meta = [("Number", expense.expense_number), ("Date", expense.date.strftime("%d/%m/%Y")),
            ("Method", expense.get_payment_method_display())]
    if expense.reference_number:
        meta.append(("Reference", expense.reference_number))
    story = _header(settings, title, meta, width)
    if expense.vendor_id:
        story.append(_party_block("Paid To", expense.vendor, []))
    elif expense.payee:
        story.append(Paragraph(f"<b>Paid To</b><br/>{_esc(expense.payee)}", NORMAL))
    story.append(Spacer(1, 8))

    def head(text, style=CELL):
        return Paragraph(f'<font color="white"><b>{text}</b></font>', style)

    rows = [
        [head("Category"), head("Description"), head("Amount", CELL_R)],
        [Paragraph(_esc(expense.category.name), CELL), Paragraph(_esc(expense.description), CELL), Paragraph(m(expense.amount), CELL_R)],
        [Paragraph("", CELL), Paragraph(f"{_esc(settings.tax_label)} @ {expense.tax_rate}%", CELL), Paragraph(m(expense.tax_amount), CELL_R)],
        [Paragraph("", CELL), Paragraph(f"<b>Total ({_esc(settings.currency_code)})</b>", CELL), Paragraph(f"<b>{m(expense.total_amount)}</b>", CELL_R)],
    ]
    t = Table(rows, colWidths=[width * 0.25, width * 0.5, width * 0.25])
    t.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, 0), HEAD_BG), ("GRID", (0, 0), (-1, -1), 0.25, GRID)]))
    story.append(t)
    if expense.notes:
        story += [Spacer(1, 6), Paragraph(f"<b>Notes:</b> {_esc(expense.notes)}", SMALL)]
    if expense.status == DocumentStatus.CANCELLED:
        story += [Spacer(1, 6), Paragraph(f"<b>Cancelled:</b> {_esc(expense.cancel_reason)}", SMALL)]
    story += [Spacer(1, 24), Paragraph(f"Printed {timezone.localtime():%d/%m/%Y %H:%M}<br/><br/>_______________________<br/>Authorised Signature", SMALL)]
    return build_pdf(story, title=f"{title} {expense.expense_number}")
