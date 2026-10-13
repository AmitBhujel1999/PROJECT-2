"""API for the financial statements (see statements.py for how figures are derived)."""

from django.utils import timezone
from rest_framework.views import APIView

from apps.common.exceptions import BusinessError
from apps.common.responses import ok
from apps.common.utils import parse_date_param
from apps.users.permissions import RolePermission

from . import statements
from .exporters import Column, export_response

COLUMNS = [Column("label", "Particulars", width=3), Column("amount", "Amount", numeric=True), Column("total", "Total", numeric=True)]
INDENT = "    "


def _period(request):
    end = parse_date_param(request, "end_date") or timezone.localdate()
    start = parse_date_param(request, "start_date")
    if start and start > end:
        raise BusinessError("Start date must be on or before the end date.", code="INVALID_PERIOD")
    return start, end


def _items(rows, sign=1):
    return [{"label": INDENT + r["name"], "amount": sign * r["amount"]} for r in rows]


class StatementView(APIView):
    permission_classes = [RolePermission]
    permission_map = {"read": "reports.view"}
    title = ""
    base_name = ""

    def respond(self, request, data, rows, meta, summary=None):
        if request.query_params.get("export"):
            response = export_response(
                request, base_name=self.base_name, title=self.title, columns=COLUMNS, rows=rows, meta=meta,
                summary=summary, landscape_mode=False,
            )
            if response:
                return response
        return ok(data)


class ProfitLossView(StatementView):
    title = "Profit & Loss Account"
    base_name = "profit-and-loss"

    def get(self, request):
        start, end = _period(request)
        d = statements.profit_and_loss(start, end)
        rows = [
            {"label": "Sales (excluding VAT)", "total": d["sales"]},
            *([{"label": "Direct income"}, *_items(d["direct_income"])] if d["direct_income"] else []),
            {"label": "Less: Cost of goods sold"},
            {"label": INDENT + "Opening stock", "amount": d["opening_stock"]},
            *([{"label": INDENT + "Opening stock added", "amount": d["opening_stock_added"]}] if d["opening_stock_added"] else []),
            {"label": INDENT + "Purchases (excluding VAT)", "amount": d["purchases"]},
            {"label": INDENT + "Less: Closing stock", "amount": -d["closing_stock"]},
            {"label": "Cost of goods sold", "total": -d["cost_of_goods_sold"]},
            *([{"label": "Direct expenses"}, *_items(d["direct_expenses"], -1)] if d["direct_expenses"] else []),
            {"label": "GROSS PROFIT", "total": d["gross_profit"]},
            *([{"label": "Add: Indirect income"}, *_items(d["indirect_income"])] if d["indirect_income"] else []),
            {"label": "Less: Expenses"},
            *_items(d["expense_categories"], -1),
            *_items(d["indirect_expenses"], -1),
            {"label": "Total expenses", "total": -d["total_indirect_expenses"]},
            {"label": "NET PROFIT" if d["net_profit"] >= 0 else "NET LOSS", "total": d["net_profit"]},
        ]
        meta = [("From", d["start_date"] or "Beginning"), ("To", d["end_date"])]
        return self.respond(request, d, rows, meta, [("Gross profit", f"{d['gross_profit']:,.2f}"), ("Net profit", f"{d['net_profit']:,.2f}")])


class BalanceSheetView(StatementView):
    title = "Balance Sheet"
    base_name = "balance-sheet"

    def get(self, request):
        as_of = parse_date_param(request, "as_of") or timezone.localdate()
        d = statements.balance_sheet(as_of)
        rows = [{"label": "CAPITAL & LIABILITIES"}]
        for g in d["liabilities"]:
            rows += [{"label": g["label"], "total": g["total"]}, *_items(g["rows"])]
        rows += [{"label": "Total capital & liabilities", "total": d["total_liabilities"]}, {"label": ""}, {"label": "ASSETS"}]
        for g in d["assets"]:
            rows += [{"label": g["label"], "total": g["total"]}, *_items(g["rows"])]
        rows.append({"label": "Total assets", "total": d["total_assets"]})
        return self.respond(request, d, rows, [("As at", as_of)])


class CashFlowView(StatementView):
    title = "Cash Flow Statement"
    base_name = "cash-flow"

    def get(self, request):
        start, end = _period(request)
        if start is None:
            raise BusinessError("Choose a start date for the cash flow.", code="START_REQUIRED")
        d = statements.cash_flow(start, end)
        rows = [{"label": "Opening cash & bank balance", "total": d["opening_cash"]}]
        for key, label in [("operating", "Operating activities"), ("investing", "Investing activities"), ("financing", "Financing activities")]:
            rows += [{"label": label}, *_items(d[key]), {"label": f"Net cash from {label.lower()}", "total": d[f"net_{key}"]}]
        rows += [
            {"label": "Net increase / (decrease) in cash", "total": d["net_change"]},
            {"label": "Closing cash & bank balance", "total": d["closing_cash"]},
        ]
        return self.respond(request, d, rows, [("From", start), ("To", end)])
