"""DEVELOPMENT / DEMO DATA ONLY — never run against a production database.

Creates demo users for every role, 10 products, 5 customers, 5 vendors and a
few months of purchases, sales (with item and invoice discounts), receipts,
vendor payments, advances, expenses and stock adjustments, spread over time so the
aging buckets are populated. All documents go through the real service layer
so stock ledgers, allocations and audit logs are consistent.
"""

import datetime as dt
import random
from decimal import Decimal

from django.core.management.base import BaseCommand, CommandError
from django.db import transaction
from django.utils import timezone

from apps.common.models import BusinessSettings
from apps.expenses.services import create_expense, ensure_default_categories
from apps.inventory.services import available_on, calculate_stock, create_stock_adjustment
from apps.parties.models import CreditTerms, Party, PartyType
from apps.payables.services import create_vendor_payment
from apps.products.models import Product
from apps.products.services import create_product
from apps.purchases.services import create_purchase
from apps.receivables.services import create_customer_receipt
from apps.sales.services import create_sale
from apps.users.models import Role, User

DEMO_PASSWORD = "Demo@12345"

PRODUCTS = [
    ("Laptop 14\" i5", "LAP-14-I5", "PCS", 65000, 78000, 13, 5),
    ("Wireless Mouse", "MOU-WL-01", "PCS", 650, 1100, 13, 20),
    ("USB-C Charger 65W", "CHG-65W", "PCS", 2200, 3400, 13, 10),
    ("A4 Paper (500 sheets)", "PAP-A4-500", "BOX", 520, 750, 13, 30),
    ("Basmati Rice", "RICE-BAS", "KG", 140, 190, 0, 100),
    ("Sunflower Oil", "OIL-SUN-1L", "LTR", 260, 330, 13, 40),
    ("LAN Cable Cat6", "CAB-CAT6", "METER", 35, 60, 13, 200),
    ("Office Chair", "CHR-OFF-01", "PCS", 7800, 11500, 13, 3),
    ("LED Monitor 24\"", "MON-24-LED", "PCS", 16500, 21000, 13, 4),
    ("Printer Toner", "TON-LJ-85A", "PCS", 2400, 3600, 13, 8),
]

CUSTOMERS = [
    ("ABC Traders", "301234567", "9801000001", "New Road, Kathmandu", CreditTerms.DAYS_30, 500000),
    ("XYZ Store", "301234568", "9801000002", "Lakeside, Pokhara", CreditTerms.DAYS_15, 200000),
    ("PQR Ltd", "301234569", "9801000003", "Butwal-8, Rupandehi", CreditTerms.DAYS_45, 800000),
    ("Himalayan Office Supplies", "301234570", "9801000004", "Patan, Lalitpur", CreditTerms.DAYS_60, 400000),
    ("Walk-in Cash Customer", "", "", "Kathmandu", CreditTerms.CASH, 0),
]

VENDORS = [
    ("ABC Suppliers", "601234567", "9851000001", "Teku, Kathmandu", CreditTerms.DAYS_30),
    ("XYZ Suppliers", "601234568", "9851000002", "Birgunj, Parsa", CreditTerms.DAYS_45),
    ("PQR Suppliers", "601234569", "9851000003", "Biratnagar, Morang", CreditTerms.DAYS_60),
    ("Everest Distributors", "601234570", "9851000004", "Bhaktapur", CreditTerms.DAYS_15),
    ("Nepal Tech Imports", "601234571", "9851000005", "Putalisadak, Kathmandu", CreditTerms.DAYS_90),
]


class Command(BaseCommand):
    help = "Load DEVELOPMENT/DEMO seed data (users, products, parties, transactions)."

    def add_arguments(self, parser):
        parser.add_argument("--if-empty", action="store_true", help="Silently skip if products already exist.")

    @transaction.atomic
    def handle(self, *args, **options):
        if Product.objects.exists():
            if options["if_empty"]:
                self.stdout.write("Demo data skipped: database already contains products.")
                return
            raise CommandError("Database already has products. Demo data should only be loaded into an empty database.")

        rng = random.Random(42)
        today = timezone.localdate()
        start = today - dt.timedelta(days=180)

        settings = BusinessSettings.get_solo()
        settings.business_name = "Demo Trading Pvt. Ltd. (DEMO DATA)"
        settings.address = "Putalisadak, Kathmandu, Nepal"
        settings.pan_vat_no = "609876543"
        settings.phone = "01-4000000"
        settings.email = "accounts@demo-trading.example"
        settings.save()

        admin = User.objects.filter(is_superuser=True).first()
        users = {}
        for role in Role:
            username = f"demo_{role.value.lower()}"
            user, created = User.objects.get_or_create(
                username=username,
                defaults={"email": f"{username}@example.com", "role": role, "first_name": "Demo", "last_name": role.label},
            )
            if created:
                user.set_password(DEMO_PASSWORD)
                user.save()
            users[role] = user
        actor = admin or users[Role.ADMIN]

        products = []
        for name, sku, unit, cost, price, tax, reorder in PRODUCTS:
            products.append(
                create_product(
                    data={
                        "name": name, "sku_code": sku, "unit": unit, "purchase_price": Decimal(cost),
                        "selling_price": Decimal(price), "tax_rate": Decimal(tax), "reorder_level": Decimal(reorder),
                        "opening_stock": Decimal(reorder * 2), "description": "Demo product",
                    },
                    user=actor,
                    opening_date=start,
                )
            )
        customers = [
            Party.objects.create(type=PartyType.CUSTOMER, name=n, pan_vat_no=pan, phone=ph, address=a, credit_terms=t,
                                 credit_limit=Decimal(lim), email=f"{n.split()[0].lower()}@example.com" if pan else "",
                                 notes="Demo customer")
            for n, pan, ph, a, t, lim in CUSTOMERS
        ]
        vendors = [
            Party.objects.create(type=PartyType.VENDOR, name=n, pan_vat_no=pan, phone=ph, address=a, credit_terms=t,
                                 email=f"{n.split()[0].lower()}@example.com", notes="Demo vendor")
            for n, pan, ph, a, t in VENDORS
        ]

        manager, accountant, staff = users[Role.MANAGER], users[Role.ACCOUNTANT], users[Role.STAFF]
        discount_choices = [(None, 0), ("PERCENTAGE", 5), ("PERCENTAGE", 10), ("FIXED", 500), (None, 0), (None, 0)]

        # Purchases: every ~9 days, restocking several products.
        purchases = []
        day = start + dt.timedelta(days=2)
        while day <= today - dt.timedelta(days=3):
            vendor = rng.choice(vendors)
            chosen = rng.sample(products, rng.randint(2, 4))
            items = []
            for p in chosen:
                dtype, dval = rng.choice(discount_choices)
                qty = Decimal(rng.randint(max(2, int(p.reorder_level)), int(p.reorder_level) * 3 + 5))
                gross = qty * p.purchase_price
                if dtype == "FIXED" and gross <= Decimal(dval):
                    dtype, dval = None, 0
                items.append({"product": p.pk, "quantity": qty, "unit_price": p.purchase_price,
                              "discount_type": dtype, "discount_value": Decimal(dval)})
            inv_dtype, inv_dval = rng.choice([(None, 0), (None, 0), ("PERCENTAGE", 2), ("FIXED", 250)])
            if inv_dtype == "FIXED" and sum(i["quantity"] * i["unit_price"] for i in items) < 5000:
                inv_dtype, inv_dval = None, 0
            purchases.append(create_purchase(
                data={"party": vendor.pk, "date": day, "items": items, "discount_type": inv_dtype,
                      "discount_value": Decimal(inv_dval), "vendor_bill_number": f"VB-{rng.randint(1000, 9999)}",
                      "notes": "Demo purchase"},
                user=rng.choice([manager, accountant]),
            ))
            day += dt.timedelta(days=rng.randint(7, 11))

        # Sales: every 1-4 days.
        sales = []
        day = start + dt.timedelta(days=5)
        while day <= today:
            customer = rng.choice(customers)
            chosen = rng.sample(products, rng.randint(1, 3))
            items = []
            for p in chosen:
                available = min(calculate_stock(p), available_on(p, day))
                if available < 2:
                    continue
                qty = Decimal(rng.randint(1, max(1, min(int(available // 3), int(p.reorder_level) + 3))))
                dtype, dval = rng.choice(discount_choices)
                if dtype == "FIXED" and qty * p.selling_price <= Decimal(dval):
                    dtype, dval = None, 0
                items.append({"product": p.pk, "quantity": qty, "discount_type": dtype, "discount_value": Decimal(dval)})
            if items:
                inv_dtype, inv_dval = rng.choice([(None, 0), (None, 0), (None, 0), ("PERCENTAGE", 3), ("FIXED", 100)])
                rough = sum(i["quantity"] * next(p.selling_price for p in chosen if p.pk == i["product"]) for i in items)
                if inv_dtype == "FIXED" and rough < 2000:
                    inv_dtype, inv_dval = None, 0
                sale, _ = create_sale(
                    data={"party": customer.pk, "date": day, "items": items, "discount_type": inv_dtype,
                          "discount_value": Decimal(inv_dval), "notes": "Demo sale"},
                    user=rng.choice([staff, manager, accountant]),
                )
                if customer.credit_terms == CreditTerms.CASH:
                    create_customer_receipt(customer_id=customer.pk, date=day, amount=sale.total_amount,
                                            payment_method="CASH", allocations=[{"document": sale.pk, "amount": sale.total_amount}],
                                            notes="Cash sale", user=accountant)
                sales.append(sale)
            day += dt.timedelta(days=rng.randint(1, 4))

        # Customer receipts: most older invoices settled (some partially), leaving recent ones and some overdue open.
        for customer in customers[:4]:
            cust_sales = sorted((s for s in sales if s.customer_id == customer.pk), key=lambda s: s.date)
            for s in cust_sales:
                s.refresh_from_db()
                age = (today - s.date).days
                if s.balance_due <= 0:
                    continue
                roll = rng.random()
                if age > 45 and roll < 0.75:
                    amount = s.balance_due
                elif age > 20 and roll < 0.4:
                    amount = (s.balance_due / 2).quantize(Decimal("0.01"))
                else:
                    continue
                pay_date = min(today, s.date + dt.timedelta(days=rng.randint(5, 40)))
                create_customer_receipt(
                    customer_id=customer.pk, date=pay_date, amount=amount,
                    payment_method=rng.choice(["BANK", "CHEQUE", "ONLINE", "CASH"]),
                    reference_number=f"REF-{rng.randint(10000, 99999)}",
                    allocations=[{"document": s.pk, "amount": amount}], user=accountant,
                )
        # A customer advance (unallocated receipt).
        create_customer_receipt(customer_id=customers[1].pk, date=today - dt.timedelta(days=3), amount=Decimal("15000"),
                                payment_method="ONLINE", reference_number="ADV-001",
                                notes="Advance received for upcoming order", user=accountant)

        # Vendor payments.
        for vendor in vendors:
            bills = sorted((p for p in purchases if p.vendor_id == vendor.pk), key=lambda p: p.date)
            for b in bills:
                b.refresh_from_db()
                age = (today - b.date).days
                roll = rng.random()
                if age > 50 and roll < 0.8:
                    amount = b.balance_due
                elif age > 25 and roll < 0.4:
                    amount = (b.balance_due * Decimal("0.6")).quantize(Decimal("0.01"))
                else:
                    continue
                if amount <= 0:
                    continue
                create_vendor_payment(
                    vendor_id=vendor.pk, date=min(today, b.date + dt.timedelta(days=rng.randint(10, 45))), amount=amount,
                    payment_method=rng.choice(["BANK", "CHEQUE", "ONLINE"]), reference_number=f"CHQ-{rng.randint(100000, 999999)}",
                    allocations=[{"document": b.pk, "amount": amount}], user=accountant,
                )
        # A vendor advance.
        create_vendor_payment(vendor_id=vendors[3].pk, date=today - dt.timedelta(days=5), amount=Decimal("20000"),
                              payment_method="BANK", reference_number="ADV-V-01", notes="Advance for next shipment",
                              user=accountant)

        # Expenses: monthly rent, salaries and utilities plus smaller day-to-day costs.
        categories = ensure_default_categories()
        month = start.replace(day=1)
        while month <= today:
            for name, description, amount, method, day_of_month in [
                ("Rent", "Shop and warehouse rent", "45000", "BANK", 1),
                ("Salaries & Wages", "Staff salaries", "120000", "BANK", 28),
                ("Utilities", "Electricity and internet", str(rng.randint(6000, 9500)), "ONLINE", 10),
            ]:
                day = month.replace(day=day_of_month)
                if start <= day <= today:
                    create_expense(data={"date": day, "category": categories[name].pk, "description": description,
                                         "amount": Decimal(amount), "payment_method": method,
                                         "payee": "Landlord" if name == "Rent" else ""}, user=accountant)
            month = (month.replace(day=28) + dt.timedelta(days=4)).replace(day=1)
        day = start + dt.timedelta(days=3)
        while day <= today:
            name, description, low, high, tax = rng.choice([
                ("Transport & Fuel", "Delivery van fuel", 1500, 4500, 0),
                ("Office Supplies", "Stationery and printer paper", 800, 3000, 13),
                ("Repairs & Maintenance", "Equipment repair", 2000, 8000, 13),
                ("Marketing & Advertising", "Social media promotion", 3000, 10000, 13),
                ("Bank Charges", "Bank service charges", 100, 600, 0),
            ])
            create_expense(
                data={"date": day, "category": categories[name].pk, "description": description,
                      "amount": Decimal(rng.randint(low, high)), "tax_rate": Decimal(tax),
                      "vendor": rng.choice(vendors).pk if name == "Repairs & Maintenance" else None,
                      "payment_method": rng.choice(["CASH", "CASH", "ONLINE"]), "notes": "Demo expense"},
                user=rng.choice([manager, accountant]),
            )
            day += dt.timedelta(days=rng.randint(4, 9))

        # Stock adjustments.
        create_stock_adjustment(product_id=products[1].pk, quantity=Decimal("-2"), reason="DAMAGED",
                                date=today - dt.timedelta(days=20), notes="Damaged in transit", user=manager)
        create_stock_adjustment(product_id=products[3].pk, quantity=Decimal("5"), reason="COUNT_CORRECTION",
                                date=today - dt.timedelta(days=10), notes="Physical count surplus", user=manager)
        create_stock_adjustment(product_id=products[5].pk, quantity=Decimal("-1"), reason="EXPIRED",
                                date=today - dt.timedelta(days=4), notes="Expired stock written off", user=manager)

        self.stdout.write(self.style.SUCCESS(
            f"DEMO DATA loaded: {len(products)} products, {len(customers)} customers, {len(vendors)} vendors, "
            f"{len(purchases)} purchases, {len(sales)} sales. Demo users: "
            + ", ".join(u.username for u in users.values()) + f" (password: {DEMO_PASSWORD})"
        ))
