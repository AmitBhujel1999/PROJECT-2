"""Reports, dashboard and global search.

Every report is computed in PostgreSQL (filters + aggregation), paginated on
the server, and can be exported with ``?export=csv`` or ``?export=pdf``
using exactly the same filters as the on-screen view.
"""

from __future__ import annotations

import datetime as dt
from decimal import Decimal

from django.db.models import Count, DecimalField, F, Q, Sum, Value
from django.db.models.functions import Coalesce, TruncDate, TruncMonth
from django.utils import timezone
from rest_framework.views import APIView

from apps.common import party_accounts as engine
from apps.common.models import DocumentStatus
from apps.common.money import money
from apps.common.pagination import StandardPagination, paginate_list
from apps.common.responses import ok
from apps.common.utils import parse_date_param, parse_int_param
from apps.expenses.models import Expense
from apps.inventory.selectors import StockStatus, ledger_row, stock_register
from apps.inventory.views import ledger_queryset, register_queryset
from apps.parties.models import Party, PartyType
from apps.payables.models import VendorPayment
from apps.payables.services import PAYABLE
from apps.products.models import Product
from apps.purchases.models import Purchase
from apps.receivables.models import CustomerReceipt
from apps.receivables.services import RECEIVABLE
from apps.sales.models import Sale, SaleItem
from apps.users.permissions import RolePermission, has_perm

from .exporters import Column, export_response

MONEY = DecimalField(max_digits=20, decimal_places=2)
QTY = DecimalField(max_digits=16, decimal_places=3)
MZERO = Value(Decimal("0.00"), output_field=MONEY)
EXPORT_LIMIT = 20000


def _meta_dates(start, end):
    meta = []
    if start:
        meta.append(("From", start.isoformat()))
    if end:
        meta.append(("To", end.isoformat()))
    return meta


# ---------------------------------------------------------------------------
# Sales / purchase reports
# ---------------------------------------------------------------------------
class TradeReportView(APIView):
    permission_classes = [RolePermission]
    permission_map = {"read": "reports.view"}

    model = None
    number_field = ""
    party_field = ""
    party_label = ""
    number_label = ""
    title = ""
    base_name = ""

    def queryset(self, request):
        qs = self.model.objects.select_related(self.party_field)
        start = parse_date_param(request, "start_date")
        end = parse_date_param(request, "end_date")
        if start:
            qs = qs.filter(date__gte=start)
        if end:
            qs = qs.filter(date__lte=end)
        party = parse_int_param(request, "party") or parse_int_param(request, self.party_field)
        if party:
            qs = qs.filter(**{f"{self.party_field}_id": party})
        number = request.query_params.get("number") or request.query_params.get(self.number_field)
        if number:
            qs = qs.filter(**{f"{self.number_field}__icontains": number})
        status = request.query_params.get("payment_status")
        if status:
            qs = qs.filter(payment_status=status)
        doc_status = request.query_params.get("status", DocumentStatus.ACTIVE)
        if doc_status != "ALL":
            qs = qs.filter(status=doc_status)
        search = request.query_params.get("search")
        if search:
            qs = qs.filter(Q(**{f"{self.number_field}__icontains": search}) | Q(**{f"{self.party_field}__name__icontains": search}))
        return qs, start, end

    def summary(self, qs) -> dict:
        agg = qs.aggregate(
            count=Count("id"),
            gross=Coalesce(Sum("subtotal"), MZERO),
            item_discount=Coalesce(Sum("item_discount_total"), MZERO),
            invoice_discount=Coalesce(Sum("discount_amount"), MZERO),
            taxable=Coalesce(Sum("taxable_amount"), MZERO),
            tax=Coalesce(Sum("tax_amount"), MZERO),
            total=Coalesce(Sum("total_amount"), MZERO),
            paid=Coalesce(Sum("amount_paid", filter=Q(status=DocumentStatus.ACTIVE)), MZERO),
            outstanding=Coalesce(Sum(F("total_amount") - F("amount_paid"), filter=Q(status=DocumentStatus.ACTIVE)), MZERO),
        )
        return {
            "total_documents": agg["count"],
            "gross_amount": money(agg["gross"]),
            "item_discount": money(agg["item_discount"]),
            "invoice_discount": money(agg["invoice_discount"]),
            "total_discount": money(agg["item_discount"] + agg["invoice_discount"]),
            "taxable_amount": money(agg["taxable"]),
            "tax_amount": money(agg["tax"]),
            "net_amount": money(agg["taxable"]),
            "total_amount": money(agg["total"]),
            "amount_paid": money(agg["paid"]),
            "outstanding": money(agg["outstanding"]),
        }

    def row(self, doc) -> dict:
        return {
            "id": doc.pk,
            "date": doc.date,
            "number": getattr(doc, self.number_field),
            "party_id": getattr(doc, f"{self.party_field}_id"),
            "party_name": getattr(doc, self.party_field).name,
            "items": doc.item_count,
            "quantity": doc.total_quantity or Decimal("0"),
            "gross_amount": doc.subtotal,
            "discount": doc.item_discount_total + doc.discount_amount,
            "taxable_amount": doc.taxable_amount,
            "tax_amount": doc.tax_amount,
            "total_amount": doc.total_amount,
            "amount_paid": doc.amount_paid,
            "balance_due": doc.balance_due,
            "due_date": doc.due_date,
            "payment_status": doc.payment_status,
            "status": doc.status,
        }

    def columns(self):
        return [
            Column("date", "Date"),
            Column("number", self.number_label),
            Column("party_name", self.party_label, width=2),
            Column("items", "Items", numeric=False, width=0.6),
            Column("quantity", "Quantity", numeric=True),
            Column("gross_amount", "Gross", numeric=True),
            Column("discount", "Discount", numeric=True),
            Column("tax_amount", "Tax", numeric=True),
            Column("total_amount", "Total", numeric=True),
            Column("balance_due", "Balance", numeric=True),
            Column("payment_status", "Payment Status"),
        ]

    def get(self, request):
        qs, start, end = self.queryset(request)
        summary = self.summary(qs)
        qs = qs.annotate(item_count=Count("items"), total_quantity=Sum("items__quantity")).order_by("-date", "-id")
        if request.query_params.get("export"):
            rows = [self.row(d) for d in qs[:EXPORT_LIMIT]]
            totals = {
                "party_name": "TOTAL",
                "gross_amount": summary["gross_amount"],
                "discount": summary["total_discount"],
                "tax_amount": summary["tax_amount"],
                "total_amount": summary["total_amount"],
                "balance_due": summary["outstanding"],
            }
            response = export_response(
                request, base_name=self.base_name, title=self.title, columns=self.columns(), rows=rows,
                meta=_meta_dates(start, end), totals=totals,
                summary=[
                    ("Documents", str(summary["total_documents"])),
                    ("Gross", f"{summary['gross_amount']:,.2f}"),
                    ("Discount", f"{summary['total_discount']:,.2f}"),
                    ("Taxable", f"{summary['taxable_amount']:,.2f}"),
                    ("Tax", f"{summary['tax_amount']:,.2f}"),
                    ("Total", f"{summary['total_amount']:,.2f}"),
                    ("Outstanding", f"{summary['outstanding']:,.2f}"),
                ],
            )
            if response:
                return response
        paginator = StandardPagination()
        page = paginator.paginate_queryset(qs, request, view=self)
        return paginator.get_paginated_response([self.row(d) for d in page], extra={"summary": summary})


class SalesReportView(TradeReportView):
    model = Sale
    number_field = "invoice_number"
    party_field = "customer"
    party_label = "Customer"
    number_label = "Invoice #"
    title = "Sales Report"
    base_name = "sales-report"


class PurchaseReportView(TradeReportView):
    model = Purchase
    number_field = "bill_number"
    party_field = "vendor"
    party_label = "Vendor"
    number_label = "Bill #"
    title = "Purchase Report"
    base_name = "purchase-report"


# ---------------------------------------------------------------------------
# Expense report
# ---------------------------------------------------------------------------
EXPENSE_COLUMNS = [
    Column("date", "Date"),
    Column("number", "Expense #"),
    Column("category_name", "Category", width=1.4),
    Column("description", "Description", width=2.2),
    Column("paid_to", "Paid To", width=1.5),
    Column("payment_method", "Method", width=0.8),
    Column("amount", "Amount", numeric=True),
    Column("tax_amount", "Tax", numeric=True),
    Column("total_amount", "Total", numeric=True),
    Column("status", "Status", width=0.8),
]


class ExpenseReportView(APIView):
    """Expenses with totals and a per-category breakdown for the same filters."""

    permission_classes = [RolePermission]
    permission_map = {"read": "reports.view"}

    def queryset(self, request):
        qs = Expense.objects.select_related("category", "vendor")
        start = parse_date_param(request, "start_date")
        end = parse_date_param(request, "end_date")
        if start:
            qs = qs.filter(date__gte=start)
        if end:
            qs = qs.filter(date__lte=end)
        category = parse_int_param(request, "category")
        if category:
            qs = qs.filter(category_id=category)
        vendor = parse_int_param(request, "vendor")
        if vendor:
            qs = qs.filter(vendor_id=vendor)
        method = request.query_params.get("payment_method")
        if method:
            qs = qs.filter(payment_method=method)
        doc_status = request.query_params.get("status", DocumentStatus.ACTIVE)
        if doc_status != "ALL":
            qs = qs.filter(status=doc_status)
        search = request.query_params.get("search")
        if search:
            qs = qs.filter(
                Q(expense_number__icontains=search) | Q(description__icontains=search) | Q(payee__icontains=search)
                | Q(vendor__name__icontains=search) | Q(reference_number__icontains=search)
            )
        return qs, start, end

    def summary(self, qs) -> dict:
        agg = qs.aggregate(
            count=Count("id"),
            amount=Coalesce(Sum("amount"), MZERO),
            tax=Coalesce(Sum("tax_amount"), MZERO),
            total=Coalesce(Sum("total_amount"), MZERO),
        )
        by_category = [
            {"category_id": r["category_id"], "category_name": r["category__name"], "count": r["count"],
             "amount": money(r["amount"]), "tax_amount": money(r["tax"]), "total_amount": money(r["total"])}
            for r in qs.order_by().values("category_id", "category__name")
            .annotate(count=Count("id"), amount=Sum("amount"), tax=Sum("tax_amount"), total=Sum("total_amount"))
            .order_by("-total", "category__name")
        ]
        return {
            "total_expenses": agg["count"],
            "amount": money(agg["amount"]),
            "tax_amount": money(agg["tax"]),
            "total_amount": money(agg["total"]),
            "by_category": by_category,
        }

    @staticmethod
    def row(e) -> dict:
        return {
            "id": e.pk,
            "date": e.date,
            "number": e.expense_number,
            "category_id": e.category_id,
            "category_name": e.category.name,
            "description": e.description,
            "paid_to": e.paid_to,
            "payment_method": e.get_payment_method_display(),
            "amount": e.amount,
            "tax_amount": e.tax_amount,
            "total_amount": e.total_amount,
            "status": e.status,
        }

    def get(self, request):
        qs, start, end = self.queryset(request)
        summary = self.summary(qs)
        qs = qs.order_by("-date", "-id")
        if request.query_params.get("export"):
            rows = [self.row(e) for e in qs[:EXPORT_LIMIT]]
            totals = {
                "description": "TOTAL",
                "amount": summary["amount"],
                "tax_amount": summary["tax_amount"],
                "total_amount": summary["total_amount"],
            }
            response = export_response(
                request, base_name="expense-report", title="Expense Report", columns=EXPENSE_COLUMNS, rows=rows,
                meta=_meta_dates(start, end), totals=totals,
                summary=[
                    ("Expenses", str(summary["total_expenses"])),
                    ("Amount", f"{summary['amount']:,.2f}"),
                    ("Tax", f"{summary['tax_amount']:,.2f}"),
                    ("Total", f"{summary['total_amount']:,.2f}"),
                ] + [(c["category_name"], f"{c['total_amount']:,.2f}") for c in summary["by_category"][:4]],
            )
            if response:
                return response
        paginator = StandardPagination()
        page = paginator.paginate_queryset(qs, request, view=self)
        return paginator.get_paginated_response([self.row(e) for e in page], extra={"summary": summary})


# ---------------------------------------------------------------------------
# Stock reports
# ---------------------------------------------------------------------------
STOCK_COLUMNS = [
    Column("sku_code", "SKU"),
    Column("name", "Item", width=2.2),
    Column("unit", "Unit", width=0.6),
    Column("total_purchased", "Purchased", numeric=True),
    Column("total_sold", "Sold", numeric=True),
    Column("current_stock", "Stock", numeric=True),
    Column("cost_value", "Cost Value", numeric=True),
    Column("retail_value", "Retail Value", numeric=True),
    Column("reorder_level", "Reorder Lvl", numeric=True),
    Column("stock_status", "Status"),
]


def stock_row(p) -> dict:
    return {
        "id": p.pk,
        "sku_code": p.sku_code,
        "name": p.name,
        "unit": p.get_unit_display(),
        "opening_stock": p.opening_stock,
        "total_purchased": p.total_purchased,
        "total_sold": p.total_sold,
        "total_adjusted": p.total_adjusted,
        "current_stock": p.current_stock,
        "purchase_price": p.purchase_price,
        "selling_price": p.selling_price,
        "cost_value": money(p.cost_value),
        "retail_value": money(p.retail_value),
        "reorder_level": p.reorder_level,
        "stock_status": p.stock_status,
        "is_active": p.is_active,
    }


class StockReportView(APIView):
    """Stock register / stock report (supports as_of, start_date, status, search, export)."""

    permission_classes = [RolePermission]
    permission_map = {"read": "inventory.view"}

    def get(self, request):
        qs, as_of, start = register_queryset(request)
        summary = qs.order_by().aggregate(
            total_products=Count("pk"),
            total_cost_value=Coalesce(Sum("cost_value"), MZERO, output_field=MONEY),
            total_retail_value=Coalesce(Sum("retail_value"), MZERO, output_field=MONEY),
            low_stock_count=Count("pk", filter=Q(stock_status=StockStatus.LOW_STOCK)),
            out_of_stock_count=Count("pk", filter=Q(stock_status=StockStatus.OUT_OF_STOCK)),
        )
        summary["total_cost_value"] = money(summary["total_cost_value"])
        summary["total_retail_value"] = money(summary["total_retail_value"])
        summary["as_of"] = as_of
        summary["start_date"] = start
        if request.query_params.get("export"):
            rows = [stock_row(p) for p in qs[:EXPORT_LIMIT]]
            meta = [("As of", (as_of or timezone.localdate()).isoformat())]
            if start:
                meta.insert(0, ("Movements from", start.isoformat()))
            response = export_response(
                request, base_name="stock-report", title="Stock Report", columns=STOCK_COLUMNS, rows=rows, meta=meta,
                totals={"name": "TOTAL", "cost_value": summary["total_cost_value"], "retail_value": summary["total_retail_value"]},
            )
            if response:
                return response
        paginator = StandardPagination()
        page = paginator.paginate_queryset(qs, request, view=self)
        return paginator.get_paginated_response([stock_row(p) for p in page], extra={"summary": summary})


LEDGER_COLUMNS = [
    Column("date", "Date"),
    Column("reference_number", "Reference"),
    Column("transaction_type_display", "Type"),
    Column("product_name", "Product", width=2),
    Column("opening_quantity", "Opening Qty", numeric=True),
    Column("quantity_in", "Qty In", numeric=True),
    Column("quantity_out", "Qty Out", numeric=True),
    Column("closing_quantity", "Closing Qty", numeric=True),
]


class StockLedgerReportView(APIView):
    permission_classes = [RolePermission]
    permission_map = {"read": "inventory.view"}

    def get(self, request):
        qs = ledger_queryset(request)
        if request.query_params.get("export"):
            rows = [ledger_row(r) for r in qs[:EXPORT_LIMIT]]
            response = export_response(
                request, base_name="stock-ledger", title="Stock Ledger", columns=LEDGER_COLUMNS, rows=rows,
                meta=_meta_dates(parse_date_param(request, "start_date"), parse_date_param(request, "end_date")),
            )
            if response:
                return response
        paginator = StandardPagination()
        page = paginator.paginate_queryset(qs, request, view=self)
        return paginator.get_paginated_response([ledger_row(r) for r in page])


# ---------------------------------------------------------------------------
# Aging reports
# ---------------------------------------------------------------------------
def aging_columns(party_label: str, unallocated_label: str):
    return [
        Column("party_name", party_label, width=2.2),
        Column("current", "Current", numeric=True),
        Column("days_1_30", "1-30", numeric=True),
        Column("days_31_60", "31-60", numeric=True),
        Column("days_61_90", "61-90", numeric=True),
        Column("days_91_120", "91-120", numeric=True),
        Column("days_over_120", "120+", numeric=True),
        Column("total", "Total", numeric=True),
        Column("unallocated", unallocated_label, numeric=True),
        Column("net_balance", "Net Balance", numeric=True),
    ]


class AgingReportView(APIView):
    permission_classes = [RolePermission]
    permission_map = {"read": "reports.view"}
    cfg: engine.AccountConfig
    title = ""
    base_name = ""
    party_label = ""
    unallocated_label = ""

    def get(self, request):
        as_of = parse_date_param(request, "as_of", timezone.localdate())
        data = engine.aging_summary(
            self.cfg, as_of, party_id=parse_int_param(request, "party"), search=request.query_params.get("search") or None
        )
        if request.query_params.get("export"):
            totals = dict(data["totals"], party_name="TOTAL")
            response = export_response(
                request, base_name=self.base_name, title=self.title,
                columns=aging_columns(self.party_label, self.unallocated_label), rows=data["rows"],
                meta=[("As of", as_of.isoformat())], totals=totals,
            )
            if response:
                return response
        rows = data.pop("rows")
        return paginate_list(request, rows, view=self, extra=data)


class ReceivablesAgingReportView(AgingReportView):
    cfg = RECEIVABLE
    title = "Accounts Receivable Aging"
    base_name = "receivables-aging"
    party_label = "Customer"
    unallocated_label = "Advances"


class PayablesAgingReportView(AgingReportView):
    cfg = PAYABLE
    title = "Accounts Payable Aging"
    base_name = "payables-aging"
    party_label = "Vendor"
    unallocated_label = "Advances"


class OutstandingReportView(APIView):
    """Receivables / payables report: open documents with balances (as of today)."""

    permission_classes = [RolePermission]
    permission_map = {"read": "reports.view"}
    cfg: engine.AccountConfig
    title = ""
    base_name = ""
    party_label = ""

    def get(self, request):
        as_of = parse_date_param(request, "as_of", timezone.localdate())
        qs = engine.docs_as_of(self.cfg, as_of, parse_int_param(request, "party")).filter(outstanding_as_of__gt=0)
        qs = qs.select_related(self.cfg.doc_party).order_by("due_date", "id")
        overdue_only = request.query_params.get("overdue") == "true"
        if overdue_only:
            qs = qs.filter(due_date__lt=as_of)
        summary = qs.aggregate(
            count=Count("id"),
            total=Coalesce(Sum("total_amount"), MZERO),
            outstanding=Coalesce(Sum("outstanding_as_of"), MZERO),
            overdue=Coalesce(Sum("outstanding_as_of", filter=Q(due_date__lt=as_of)), MZERO),
        )

        def row(d):
            days = (as_of - d.due_date).days
            return {
                "id": d.pk,
                "number": self.cfg.doc_no(d),
                "date": d.date,
                "due_date": d.due_date,
                "party_id": getattr(d, f"{self.cfg.doc_party}_id"),
                "party_name": getattr(d, self.cfg.doc_party).name,
                "total_amount": d.total_amount,
                "paid": money(d.allocated_as_of),
                "outstanding": money(d.outstanding_as_of),
                "days_overdue": max(days, 0),
                "bucket": engine.bucket_for(days),
                "payment_status": d.payment_status,
            }

        if request.query_params.get("export"):
            cols = [
                Column("number", "Number"), Column("date", "Date"), Column("due_date", "Due Date"),
                Column("party_name", self.party_label, width=2), Column("total_amount", "Total", numeric=True),
                Column("paid", "Paid", numeric=True), Column("outstanding", "Outstanding", numeric=True),
                Column("days_overdue", "Days Overdue"),
            ]
            response = export_response(
                request, base_name=self.base_name, title=self.title, columns=cols,
                rows=[row(d) for d in qs[:EXPORT_LIMIT]], meta=[("As of", as_of.isoformat())],
                totals={"party_name": "TOTAL", "total_amount": summary["total"], "outstanding": summary["outstanding"]},
            )
            if response:
                return response
        paginator = StandardPagination()
        page = paginator.paginate_queryset(qs, request, view=self)
        return paginator.get_paginated_response([row(d) for d in page], extra={"summary": summary, "as_of": as_of})


class ReceivablesReportView(OutstandingReportView):
    cfg = RECEIVABLE
    title = "Receivables Report"
    base_name = "receivables-report"
    party_label = "Customer"


class PayablesReportView(OutstandingReportView):
    cfg = PAYABLE
    title = "Payables Report"
    base_name = "payables-report"
    party_label = "Vendor"


# ---------------------------------------------------------------------------
# Dashboard
# ---------------------------------------------------------------------------
class DashboardView(APIView):
    permission_classes = [RolePermission]
    permission_map = {"read": "dashboard.view"}

    def get(self, request):
        today = timezone.localdate()
        end = parse_date_param(request, "end_date", today)
        start = parse_date_param(request, "start_date", end - dt.timedelta(days=29))
        if start > end:
            start, end = end, start
        user = request.user
        finance = has_perm(user, "reports.view")

        def day_total(model, field, date):
            return money(
                model.objects.filter(date=date, status=DocumentStatus.ACTIVE).aggregate(s=Coalesce(Sum(field), MZERO))["s"]
            )

        def period_total(model, field):
            return money(
                model.objects.filter(date__gte=start, date__lte=end, status=DocumentStatus.ACTIVE)
                .aggregate(s=Coalesce(Sum(field), MZERO))["s"]
            )

        stock = stock_register(Product.objects.filter(is_active=True)).order_by().aggregate(
            cost=Coalesce(Sum("cost_value"), MZERO, output_field=MONEY),
            retail=Coalesce(Sum("retail_value"), MZERO, output_field=MONEY),
            low=Count("pk", filter=Q(stock_status__in=[StockStatus.LOW_STOCK, StockStatus.OUT_OF_STOCK])),
        )
        low_stock = [
            {"id": p.pk, "name": p.name, "sku_code": p.sku_code, "current_stock": p.current_stock,
             "reorder_level": p.reorder_level, "unit": p.get_unit_display(), "stock_status": p.stock_status}
            for p in stock_register(Product.objects.filter(is_active=True))
            .filter(stock_status__in=[StockStatus.LOW_STOCK, StockStatus.OUT_OF_STOCK])
            .order_by("current_stock", "name")[:10]
        ]
        cards = {
            "today": today,
            "todays_sales": day_total(Sale, "total_amount", today),
            "todays_purchases": day_total(Purchase, "total_amount", today),
            "todays_receipts": day_total(CustomerReceipt, "amount", today),
            "todays_payments": day_total(VendorPayment, "amount", today),
            "period_sales": period_total(Sale, "total_amount"),
            "period_purchases": period_total(Purchase, "total_amount"),
            "period_receipts": period_total(CustomerReceipt, "amount"),
            "period_payments": period_total(VendorPayment, "amount"),
            "stock_value": money(stock["cost"]),
            "stock_retail_value": money(stock["retail"]),
            "low_stock_count": stock["low"],
            "total_customers": Party.objects.filter(type=PartyType.CUSTOMER, is_active=True).count(),
            "total_vendors": Party.objects.filter(type=PartyType.VENDOR, is_active=True).count(),
        }
        if has_perm(user, "expenses.view"):
            cards.update(
                todays_expenses=day_total(Expense, "total_amount", today),
                period_expenses=period_total(Expense, "total_amount"),
            )
        if finance:
            rec = engine.totals_overview(RECEIVABLE)
            pay = engine.totals_overview(PAYABLE)
            cards.update(
                receivables=rec["outstanding"], overdue_receivables=rec["overdue"], customer_advances=rec["unallocated"],
                payables=pay["outstanding"], overdue_payables=pay["overdue"], vendor_advances=pay["unallocated"],
            )

        monthly = (end - start).days > 92
        trunc = TruncMonth if monthly else TruncDate

        def series(model, field):
            rows = (
                model.objects.filter(date__gte=start, date__lte=end, status=DocumentStatus.ACTIVE)
                .annotate(bucket=trunc("date")).values("bucket").annotate(total=Sum(field)).order_by("bucket")
            )
            return {(r["bucket"].date() if isinstance(r["bucket"], dt.datetime) else r["bucket"]): r["total"] for r in rows}

        labels = []
        cursor = start.replace(day=1) if monthly else start
        while cursor <= end:
            labels.append(cursor)
            if monthly:
                cursor = (cursor.replace(day=28) + dt.timedelta(days=4)).replace(day=1)
            else:
                cursor += dt.timedelta(days=1)
        sales_s, purchase_s = series(Sale, "total_amount"), series(Purchase, "total_amount")
        receipt_s, payment_s = series(CustomerReceipt, "amount"), series(VendorPayment, "amount")
        expense_s = series(Expense, "total_amount")
        trend = [
            {
                "date": d,
                "sales": money(sales_s.get(d, 0)),
                "purchases": money(purchase_s.get(d, 0)),
                "receipts": money(receipt_s.get(d, 0)),
                "payments": money(payment_s.get(d, 0)),
                "expenses": money(expense_s.get(d, 0)),
            }
            for d in labels
        ]
        top_products = [
            {"product_id": r["product_id"], "name": r["product__name"], "sku_code": r["product__sku_code"],
             "quantity": r["qty"], "amount": money(r["amount"])}
            for r in SaleItem.objects.filter(sale__date__gte=start, sale__date__lte=end, sale__status=DocumentStatus.ACTIVE)
            .values("product_id", "product__name", "product__sku_code")
            .annotate(qty=Sum("quantity"), amount=Sum("taxable_amount"))
            .order_by("-amount")[:10]
        ]
        stock_value = [
            {"product_id": p.pk, "name": p.name, "sku_code": p.sku_code, "value": money(p.cost_value), "quantity": p.current_stock}
            for p in stock_register(Product.objects.filter(is_active=True)).filter(current_stock__gt=0).order_by("-cost_value")[:10]
        ]
        recent_sales = [
            {"id": s.pk, "number": s.invoice_number, "date": s.date, "party_name": s.customer.name,
             "total_amount": s.total_amount, "payment_status": s.payment_status}
            for s in Sale.objects.select_related("customer").filter(status=DocumentStatus.ACTIVE).order_by("-date", "-id")[:5]
        ]
        return ok(
            {
                "start_date": start,
                "end_date": end,
                "granularity": "month" if monthly else "day",
                "cards": cards,
                "trend": trend,
                "top_products": top_products,
                "stock_value_by_product": stock_value,
                "low_stock": low_stock,
                "recent_sales": recent_sales,
            }
        )


# ---------------------------------------------------------------------------
# Global search
# ---------------------------------------------------------------------------
class GlobalSearchView(APIView):
    """Search products, customers, vendors, invoices, bills, receipts, payments and expenses.

    ``?q=`` searches all types (top 5 each). ``?q=&type=<type>`` returns one
    type with full server-side pagination.
    """

    permission_classes = [RolePermission]
    permission_map = {"read": "search.use"}

    def sources(self, q: str, user):
        sources = {
            "products": (
                "products.view",
                Product.objects.filter(Q(name__icontains=q) | Q(sku_code__icontains=q)).order_by("name"),
                lambda p: {"id": p.pk, "title": p.name, "subtitle": f"SKU {p.sku_code}", "url": f"/products/{p.pk}", "is_active": p.is_active},
            ),
            "customers": (
                "parties.view",
                Party.objects.filter(type=PartyType.CUSTOMER).filter(
                    Q(name__icontains=q) | Q(phone__icontains=q) | Q(pan_vat_no__icontains=q) | Q(email__icontains=q)
                ).order_by("name"),
                lambda p: {"id": p.pk, "title": p.name, "subtitle": " · ".join(x for x in [p.phone, p.pan_vat_no and f"PAN {p.pan_vat_no}"] if x), "url": f"/customers/{p.pk}", "is_active": p.is_active},
            ),
            "vendors": (
                "parties.view",
                Party.objects.filter(type=PartyType.VENDOR).filter(
                    Q(name__icontains=q) | Q(phone__icontains=q) | Q(pan_vat_no__icontains=q) | Q(email__icontains=q)
                ).order_by("name"),
                lambda p: {"id": p.pk, "title": p.name, "subtitle": " · ".join(x for x in [p.phone, p.pan_vat_no and f"PAN {p.pan_vat_no}"] if x), "url": f"/vendors/{p.pk}", "is_active": p.is_active},
            ),
            "invoices": (
                "sales.view",
                Sale.objects.select_related("customer").filter(Q(invoice_number__icontains=q) | Q(customer__name__icontains=q)).order_by("-date", "-id"),
                lambda s: {"id": s.pk, "title": s.invoice_number, "subtitle": f"{s.customer.name} · {s.date} · {s.total_amount}", "url": f"/sales/{s.pk}", "status": s.payment_status},
            ),
            "bills": (
                "purchases.view",
                Purchase.objects.select_related("vendor").filter(
                    Q(bill_number__icontains=q) | Q(vendor_bill_number__icontains=q) | Q(vendor__name__icontains=q)
                ).order_by("-date", "-id"),
                lambda p: {"id": p.pk, "title": p.bill_number, "subtitle": f"{p.vendor.name} · {p.date} · {p.total_amount}", "url": f"/purchases/{p.pk}", "status": p.payment_status},
            ),
            "receipts": (
                "receipts.view",
                CustomerReceipt.objects.select_related("customer").filter(
                    Q(receipt_number__icontains=q) | Q(reference_number__icontains=q) | Q(customer__name__icontains=q)
                ).order_by("-date", "-id"),
                lambda r: {"id": r.pk, "title": r.receipt_number, "subtitle": f"{r.customer.name} · {r.date} · {r.amount}", "url": f"/receipts/{r.pk}"},
            ),
            "payments": (
                "payments.view",
                VendorPayment.objects.select_related("vendor").filter(
                    Q(payment_number__icontains=q) | Q(reference_number__icontains=q) | Q(vendor__name__icontains=q)
                ).order_by("-date", "-id"),
                lambda r: {"id": r.pk, "title": r.payment_number, "subtitle": f"{r.vendor.name} · {r.date} · {r.amount}", "url": f"/payments/{r.pk}"},
            ),
            "expenses": (
                "expenses.view",
                Expense.objects.select_related("category", "vendor").filter(
                    Q(expense_number__icontains=q) | Q(description__icontains=q) | Q(payee__icontains=q)
                    | Q(reference_number__icontains=q) | Q(vendor__name__icontains=q)
                ).order_by("-date", "-id"),
                lambda e: {"id": e.pk, "title": e.expense_number, "subtitle": f"{e.category.name} · {e.description} · {e.date} · {e.total_amount}", "url": f"/expenses/{e.pk}", "status": e.status},
            ),
        }
        return {k: v for k, v in sources.items() if has_perm(user, v[0])}

    def get(self, request):
        q = (request.query_params.get("q") or "").strip()
        if len(q) < 2:
            return ok({"query": q, "groups": []})
        sources = self.sources(q[:100], request.user)
        kind = request.query_params.get("type")
        if kind:
            if kind not in sources:
                return ok({"query": q, "groups": []})
            _, qs, fmt_row = sources[kind]
            paginator = StandardPagination()
            page = paginator.paginate_queryset(qs, request, view=self)
            return paginator.get_paginated_response([fmt_row(x) for x in page], extra={"type": kind, "query": q})
        groups = []
        for key, (_, qs, fmt_row) in sources.items():
            items = list(qs[:5])
            if items:
                groups.append({"type": key, "count": qs.count(), "results": [fmt_row(x) for x in items]})
        return ok({"query": q, "groups": groups})
