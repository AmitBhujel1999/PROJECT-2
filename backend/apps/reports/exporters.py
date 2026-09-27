"""CSV and PDF export helpers (reportlab, no external services)."""

from __future__ import annotations

import csv
import datetime as dt
import io
from dataclasses import dataclass
from decimal import Decimal

from django.http import HttpResponse
from django.utils import timezone
from reportlab.lib import colors
from reportlab.lib.enums import TA_RIGHT
from reportlab.lib.pagesizes import A4, landscape
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle

from apps.common.models import BusinessSettings


@dataclass
class Column:
    key: str
    label: str
    numeric: bool = False
    width: float | None = None  # relative weight for PDF


def fmt(value, numeric=False) -> str:
    if value is None:
        return ""
    if isinstance(value, Decimal):
        return f"{value:,.2f}" if numeric else str(value)
    if isinstance(value, dt.datetime):
        return timezone.localtime(value).strftime("%Y-%m-%d %H:%M")
    if isinstance(value, dt.date):
        return value.strftime("%Y-%m-%d")
    if numeric:
        try:
            return f"{Decimal(str(value)):,.2f}"
        except Exception:
            return str(value)
    return str(value)


def _csv_safe(value: str) -> str:
    """Neutralise spreadsheet formula injection for text cells."""
    if value and value[0] in "=+-@\t\r":
        try:
            Decimal(value.replace(",", ""))
            return value  # a genuine negative number
        except Exception:
            return "'" + value
    return value


def export_filename(base: str, ext: str) -> str:
    return f"{base}-{timezone.localdate():%Y%m%d}.{ext}"


def csv_response(filename: str, columns: list[Column], rows: list[dict], *, title: str | None = None,
                 meta: list[tuple[str, str]] | None = None, totals: dict | None = None) -> HttpResponse:
    buffer = io.StringIO()
    buffer.write("﻿")  # UTF-8 BOM so Excel opens Unicode correctly
    writer = csv.writer(buffer)
    if title:
        writer.writerow([title])
    for label, value in meta or []:
        writer.writerow([label, _csv_safe(str(value))])
    if title or meta:
        writer.writerow([])
    writer.writerow([c.label for c in columns])
    for row in rows:
        writer.writerow([_csv_safe(_plain(row.get(c.key))) for c in columns])
    if totals:
        writer.writerow([_csv_safe(_plain(totals.get(c.key, ""))) for c in columns])
    response = HttpResponse(buffer.getvalue(), content_type="text/csv; charset=utf-8")
    response["Content-Disposition"] = f'attachment; filename="{filename}"'
    return response


def _plain(value) -> str:
    if value is None:
        return ""
    if isinstance(value, Decimal):
        return str(value)
    if isinstance(value, dt.datetime):
        return timezone.localtime(value).strftime("%Y-%m-%d %H:%M")
    if isinstance(value, dt.date):
        return value.isoformat()
    return str(value)


# ---------------------------------------------------------------------------
# PDF
# ---------------------------------------------------------------------------
_styles = getSampleStyleSheet()
H1 = ParagraphStyle("h1", parent=_styles["Heading1"], fontSize=15, spaceAfter=2)
H2 = ParagraphStyle("h2", parent=_styles["Heading2"], fontSize=11, spaceAfter=2)
SMALL = ParagraphStyle("small", parent=_styles["Normal"], fontSize=8, leading=10)
CELL = ParagraphStyle("cell", parent=_styles["Normal"], fontSize=7.5, leading=9)
CELL_R = ParagraphStyle("cellr", parent=CELL, alignment=TA_RIGHT)
NORMAL = ParagraphStyle("normal", parent=_styles["Normal"], fontSize=9, leading=11)
RIGHT = ParagraphStyle("right", parent=NORMAL, alignment=TA_RIGHT)

GRID = colors.HexColor("#cbd5e1")
HEAD_BG = colors.HexColor("#1e293b")
ZEBRA = colors.HexColor("#f1f5f9")


def _esc(text: str) -> str:
    return (text or "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def company_header(settings: BusinessSettings) -> list:
    parts = [Paragraph(_esc(settings.business_name), H1)]
    line = " | ".join(x for x in [settings.address, f"PAN/VAT: {settings.pan_vat_no}" if settings.pan_vat_no else "",
                                   settings.phone, settings.email] if x)
    if line:
        parts.append(Paragraph(_esc(line), SMALL))
    return parts


def _footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 7)
    canvas.setFillColor(colors.grey)
    canvas.drawString(12 * mm, 8 * mm, f"Generated {timezone.localtime():%Y-%m-%d %H:%M}")
    canvas.drawRightString(doc.pagesize[0] - 12 * mm, 8 * mm, f"Page {doc.page}")
    canvas.restoreState()


def build_pdf(story: list, *, landscape_mode=False, title="Report") -> bytes:
    buffer = io.BytesIO()
    pagesize = landscape(A4) if landscape_mode else A4
    doc = SimpleDocTemplate(
        buffer, pagesize=pagesize, leftMargin=12 * mm, rightMargin=12 * mm, topMargin=12 * mm, bottomMargin=14 * mm,
        title=title, author="Accounting & Inventory System",
    )
    doc.build(story, onFirstPage=_footer, onLaterPages=_footer)
    return buffer.getvalue()


def data_table(columns: list[Column], rows: list[dict], totals: dict | None, available_width: float) -> Table:
    header = [Paragraph(f'<font color="white"><b>{_esc(c.label)}</b></font>', CELL_R if c.numeric else CELL) for c in columns]
    body = [
        [Paragraph(_esc(fmt(row.get(c.key), c.numeric)), CELL_R if c.numeric else CELL) for c in columns] for row in rows
    ]
    data = [header, *body]
    if totals:
        data.append([Paragraph(f"<b>{_esc(fmt(totals.get(c.key), c.numeric))}</b>", CELL_R if c.numeric else CELL) for c in columns])
    weights = [c.width or (1.0 if c.numeric else 1.3) for c in columns]
    total_weight = sum(weights)
    widths = [available_width * w / total_weight for w in weights]
    table = Table(data, colWidths=widths, repeatRows=1)
    style = [
        ("BACKGROUND", (0, 0), (-1, 0), HEAD_BG),
        ("GRID", (0, 0), (-1, -1), 0.25, GRID),
        ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
        ("TOPPADDING", (0, 0), (-1, -1), 2),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 2),
    ]
    for i in range(1, len(body) + 1):
        if i % 2 == 0:
            style.append(("BACKGROUND", (0, i), (-1, i), ZEBRA))
    if totals:
        style.append(("BACKGROUND", (0, len(data) - 1), (-1, len(data) - 1), colors.HexColor("#e2e8f0")))
    table.setStyle(TableStyle(style))
    return table


def pdf_report_response(filename: str, title: str, columns: list[Column], rows: list[dict], *,
                        meta: list[tuple[str, str]] | None = None, totals: dict | None = None,
                        summary: list[tuple[str, str]] | None = None, landscape_mode=True, inline=False) -> HttpResponse:
    settings = BusinessSettings.get_solo()
    story = company_header(settings)
    story += [Spacer(1, 4), Paragraph(_esc(title), H2)]
    if meta:
        story.append(Paragraph(" &nbsp;&nbsp; ".join(f"<b>{_esc(k)}:</b> {_esc(str(v))}" for k, v in meta), SMALL))
    if summary:
        story.append(Spacer(1, 4))
        cells = [[Paragraph(f"<b>{_esc(k)}</b><br/>{_esc(v)}", SMALL) for k, v in summary]]
        t = Table(cells)
        t.setStyle(TableStyle([("BOX", (0, 0), (-1, -1), 0.25, GRID), ("INNERGRID", (0, 0), (-1, -1), 0.25, GRID)]))
        story.append(t)
    story.append(Spacer(1, 6))
    pagesize = landscape(A4) if landscape_mode else A4
    width = pagesize[0] - 24 * mm
    if rows:
        story.append(data_table(columns, rows, totals, width))
    else:
        story.append(Paragraph("No records for the selected filters.", NORMAL))
    pdf = build_pdf(story, landscape_mode=landscape_mode, title=title)
    return pdf_response(pdf, filename, inline=inline)


def pdf_response(pdf: bytes, filename: str, *, inline=False) -> HttpResponse:
    response = HttpResponse(pdf, content_type="application/pdf")
    disposition = "inline" if inline else "attachment"
    response["Content-Disposition"] = f'{disposition}; filename="{filename}"'
    return response


def export_response(request, *, base_name: str, title: str, columns: list[Column], rows: list[dict],
                    meta=None, totals=None, summary=None, landscape_mode=True):
    """Return a CSV/PDF response if ``?export=csv|pdf`` was requested, else None."""
    kind = request.query_params.get("export")
    if kind == "csv":
        return csv_response(export_filename(base_name, "csv"), columns, rows, title=title, meta=meta, totals=totals)
    if kind == "pdf":
        return pdf_report_response(
            export_filename(base_name, "pdf"), title, columns, rows, meta=meta, totals=totals,
            summary=summary, landscape_mode=landscape_mode, inline=request.query_params.get("inline") == "1",
        )
    return None
