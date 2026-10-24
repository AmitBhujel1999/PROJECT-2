namespace Accounting.Core.Data;

/// <summary>
/// Database migrations. Each entry runs once, in order; PRAGMA user_version
/// records how many have been applied. Never edit a shipped migration: append a new one.
///
/// Storage rules (so no rounding drift is possible):
/// * money        INTEGER paisa       (Rs. 12.34 = 1234)
/// * quantities   INTEGER thousandths (2.5 Kg   = 2500)
/// * rates        INTEGER hundredths  (13 %     = 1300)
/// * dates        TEXT 'yyyy-MM-dd', timestamps TEXT ISO-8601 UTC
/// </summary>
internal static class Schema
{
    private const string IsDate = "GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'";

    public static readonly string[] Migrations =
    [
        // 1: core, users, audit, products, parties, inventory
        $"""
        CREATE TABLE business_settings (
            id               INTEGER PRIMARY KEY CHECK (id = 1),
            business_name    TEXT NOT NULL DEFAULT 'My Business Pvt. Ltd.',
            address          TEXT NOT NULL DEFAULT 'Kathmandu, Nepal',
            pan_vat_no       TEXT NOT NULL DEFAULT '',
            phone            TEXT NOT NULL DEFAULT '',
            email            TEXT NOT NULL DEFAULT '',
            currency_code    TEXT NOT NULL DEFAULT 'NPR',
            currency_symbol  TEXT NOT NULL DEFAULT 'Rs.',
            tax_label        TEXT NOT NULL DEFAULT 'VAT',
            default_tax_rate INTEGER NOT NULL DEFAULT 1300 CHECK (default_tax_rate BETWEEN 0 AND 10000),
            invoice_footer   TEXT NOT NULL DEFAULT 'Thank you for your business.',
            date_system      TEXT NOT NULL DEFAULT '' CHECK (date_system IN ('', 'AD', 'BS')),
            updated_at       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        INSERT INTO business_settings (id) VALUES (1);

        CREATE TABLE document_sequences (
            key         TEXT PRIMARY KEY,
            prefix      TEXT NOT NULL,
            padding     INTEGER NOT NULL DEFAULT 6,
            last_number INTEGER NOT NULL DEFAULT 0 CHECK (last_number >= 0)
        );
        INSERT INTO document_sequences (key, prefix) VALUES
            ('sale', 'INV'), ('purchase', 'BILL'), ('customer_receipt', 'RCPT'), ('vendor_payment', 'PAY'),
            ('stock_adjustment', 'ADJ'), ('expense', 'EXP'), ('contra', 'CNTR'), ('journal', 'JV');

        CREATE TABLE users (
            id            INTEGER PRIMARY KEY,
            username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
            full_name     TEXT NOT NULL DEFAULT '',
            email         TEXT NOT NULL DEFAULT '',
            phone         TEXT NOT NULL DEFAULT '',
            role          TEXT NOT NULL DEFAULT 'STAFF' CHECK (role IN ('ADMIN', 'MANAGER', 'ACCOUNTANT', 'STAFF')),
            password_hash TEXT NOT NULL,
            is_active     INTEGER NOT NULL DEFAULT 1,
            last_login    TEXT,
            created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );

        CREATE TABLE audit_log (
            id          INTEGER PRIMARY KEY,
            user_id     INTEGER REFERENCES users (id) ON DELETE SET NULL,
            username    TEXT NOT NULL DEFAULT '',
            action      TEXT NOT NULL,
            model_name  TEXT NOT NULL,
            object_id   TEXT NOT NULL DEFAULT '',
            object_repr TEXT NOT NULL DEFAULT '',
            timestamp   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            ip_address  TEXT,
            before_data TEXT,
            after_data  TEXT
        );
        CREATE INDEX audit_log_model_object ON audit_log (model_name, object_id);
        CREATE INDEX audit_log_timestamp ON audit_log (timestamp);
        CREATE TRIGGER audit_log_no_update BEFORE UPDATE ON audit_log
            BEGIN SELECT RAISE(ABORT, 'Audit log entries are immutable.'); END;
        CREATE TRIGGER audit_log_no_delete BEFORE DELETE ON audit_log
            BEGIN SELECT RAISE(ABORT, 'Audit log entries cannot be deleted.'); END;

        CREATE TABLE products (
            id             INTEGER PRIMARY KEY,
            name           TEXT NOT NULL CHECK (length(trim(name)) > 0),
            sku_code       TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(trim(sku_code)) > 0),
            unit           TEXT NOT NULL DEFAULT 'PCS'
                           CHECK (unit IN ('PCS', 'KG', 'BOX', 'LTR', 'METER', 'SET', 'PACK', 'DOZEN')),
            description    TEXT NOT NULL DEFAULT '',
            opening_stock  INTEGER NOT NULL DEFAULT 0 CHECK (opening_stock >= 0),
            opening_rate   INTEGER NOT NULL DEFAULT 0 CHECK (opening_rate >= 0),
            reorder_level  INTEGER NOT NULL DEFAULT 0 CHECK (reorder_level >= 0),
            purchase_price INTEGER NOT NULL DEFAULT 0 CHECK (purchase_price >= 0),
            selling_price  INTEGER NOT NULL DEFAULT 0 CHECK (selling_price >= 0),
            tax_rate       INTEGER NOT NULL DEFAULT 1300 CHECK (tax_rate BETWEEN 0 AND 10000),
            is_active      INTEGER NOT NULL DEFAULT 1,
            created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        CREATE INDEX products_name ON products (name COLLATE NOCASE);

        CREATE TABLE parties (
            id              INTEGER PRIMARY KEY,
            type            TEXT NOT NULL CHECK (type IN ('CUSTOMER', 'VENDOR')),
            name            TEXT NOT NULL CHECK (length(trim(name)) > 0),
            pan_vat_no      TEXT NOT NULL DEFAULT '',
            phone           TEXT NOT NULL DEFAULT '',
            email           TEXT NOT NULL DEFAULT '',
            address         TEXT NOT NULL DEFAULT '',
            credit_terms    TEXT NOT NULL DEFAULT 'CASH'
                            CHECK (credit_terms IN ('CASH', 'DAYS_7', 'DAYS_15', 'DAYS_30', 'DAYS_45', 'DAYS_60', 'DAYS_90', 'CUSTOM')),
            credit_days     INTEGER NOT NULL DEFAULT 0 CHECK (credit_days BETWEEN 0 AND 3650),
            credit_limit    INTEGER NOT NULL DEFAULT 0 CHECK (credit_limit >= 0),
            opening_balance INTEGER NOT NULL DEFAULT 0 CHECK (opening_balance >= 0),
            opening_side    TEXT NOT NULL DEFAULT 'DR' CHECK (opening_side IN ('DR', 'CR')),
            notes           TEXT NOT NULL DEFAULT '',
            is_active       INTEGER NOT NULL DEFAULT 1,
            created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        CREATE INDEX parties_type_name ON parties (type, name COLLATE NOCASE);
        CREATE INDEX parties_phone ON parties (phone);
        CREATE INDEX parties_pan ON parties (pan_vat_no);
        CREATE TRIGGER parties_type_fixed BEFORE UPDATE OF type ON parties WHEN NEW.type <> OLD.type
            BEGIN SELECT RAISE(ABORT, 'A party''s type cannot change.'); END;

        CREATE TABLE stock_movements (
            id               INTEGER PRIMARY KEY,
            product_id       INTEGER NOT NULL REFERENCES products (id) ON DELETE RESTRICT,
            date             TEXT NOT NULL CHECK (date {IsDate}),
            transaction_type TEXT NOT NULL CHECK (transaction_type IN
                             ('OPENING_STOCK', 'PURCHASE', 'SALE', 'ADJUSTMENT', 'PURCHASE_CANCEL', 'SALE_CANCEL')),
            reference_type   TEXT NOT NULL DEFAULT '',
            reference_id     INTEGER,
            reference_number TEXT NOT NULL DEFAULT '',
            quantity_in      INTEGER NOT NULL DEFAULT 0 CHECK (quantity_in >= 0),
            quantity_out     INTEGER NOT NULL DEFAULT 0 CHECK (quantity_out >= 0),
            running_balance  INTEGER NOT NULL CHECK (running_balance >= 0),
            unit_cost        INTEGER NOT NULL DEFAULT 0,
            notes            TEXT NOT NULL DEFAULT '',
            created_at       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            created_by       INTEGER REFERENCES users (id) ON DELETE RESTRICT,
            CHECK ((quantity_in > 0 AND quantity_out = 0) OR (quantity_out > 0 AND quantity_in = 0))
        );
        CREATE INDEX stock_movements_product_date ON stock_movements (product_id, date, id);
        CREATE INDEX stock_movements_reference ON stock_movements (reference_type, reference_id);
        CREATE INDEX stock_movements_reference_number ON stock_movements (reference_number);
        CREATE TRIGGER stock_movements_no_update BEFORE UPDATE ON stock_movements
            BEGIN SELECT RAISE(ABORT, 'Stock ledger entries are immutable.'); END;
        CREATE TRIGGER stock_movements_no_delete BEFORE DELETE ON stock_movements
            BEGIN SELECT RAISE(ABORT, 'Stock ledger entries are immutable.'); END;

        CREATE TABLE stock_adjustments (
            id                INTEGER PRIMARY KEY,
            adjustment_number TEXT NOT NULL UNIQUE,
            product_id        INTEGER NOT NULL REFERENCES products (id) ON DELETE RESTRICT,
            date              TEXT NOT NULL CHECK (date {IsDate}),
            quantity          INTEGER NOT NULL CHECK (quantity <> 0),
            reason            TEXT NOT NULL CHECK (reason IN
                              ('DAMAGED', 'EXPIRED', 'LOST', 'COUNT_CORRECTION', 'FOUND', 'RETURN', 'OTHER')),
            notes             TEXT NOT NULL DEFAULT '',
            stock_before      INTEGER NOT NULL,
            stock_after       INTEGER NOT NULL,
            created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            created_by        INTEGER REFERENCES users (id) ON DELETE RESTRICT
        );
        CREATE INDEX stock_adjustments_date ON stock_adjustments (date, id);
        """,

        // 2: cash/bank accounts, sales, purchases, receipts, vendor payments, allocations
        """
        CREATE TABLE money_accounts (
            id              INTEGER PRIMARY KEY,
            name            TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(trim(name)) > 0),
            kind            TEXT NOT NULL DEFAULT 'BANK' CHECK (kind IN ('CASH', 'BANK', 'WALLET', 'OTHER')),
            account_number  TEXT NOT NULL DEFAULT '',
            opening_balance INTEGER NOT NULL DEFAULT 0,
            notes           TEXT NOT NULL DEFAULT '',
            is_active       INTEGER NOT NULL DEFAULT 1,
            created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        """
        + TradeDocumentTables("sales", "sale_items", extraColumns: "")
        + TradeDocumentTables("purchases", "purchase_items", extraColumns: "vendor_bill_number TEXT NOT NULL DEFAULT '',")
        + "CREATE INDEX purchases_vendor_bill_number ON purchases (vendor_bill_number);"
        + PaymentTables("customer_receipts", "receipt_allocations", "sales")
        + PaymentTables("vendor_payments", "payment_allocations", "purchases"),

        // 3: expenses, contra entries, chart of accounts and journal vouchers
        $"""
        CREATE TABLE expense_categories (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(trim(name)) > 0),
            description TEXT NOT NULL DEFAULT '',
            is_active   INTEGER NOT NULL DEFAULT 1,
            created_at  TEXT NOT NULL DEFAULT {Now},
            updated_at  TEXT NOT NULL DEFAULT {Now}
        );
        INSERT INTO expense_categories (name, description) VALUES
            ('Rent', 'Office, shop and warehouse rent'),
            ('Salaries & Wages', 'Staff salaries, wages and allowances'),
            ('Utilities', 'Electricity, water, internet and phone'),
            ('Transport & Fuel', 'Freight, delivery, vehicle fuel and travel'),
            ('Office Supplies', 'Stationery, printing and small office items'),
            ('Repairs & Maintenance', 'Repairs to premises, equipment and vehicles'),
            ('Marketing & Advertising', 'Ads, promotions and signage'),
            ('Bank Charges', 'Bank fees, commissions and service charges'),
            ('Professional Fees', 'Audit, legal and consulting fees'),
            ('Taxes & Licenses', 'Registration renewals, licenses and non-recoverable taxes'),
            ('Miscellaneous', 'Expenses that fit no other category');

        CREATE TABLE expenses (
            id               INTEGER PRIMARY KEY,
            number           TEXT NOT NULL UNIQUE,
            date             TEXT NOT NULL CHECK (date {IsDate}),
            category_id      INTEGER NOT NULL REFERENCES expense_categories (id) ON DELETE RESTRICT,
            vendor_id        INTEGER REFERENCES parties (id) ON DELETE RESTRICT,
            payee            TEXT NOT NULL DEFAULT '',
            description      TEXT NOT NULL CHECK (length(trim(description)) > 0),
            amount           INTEGER NOT NULL CHECK (amount > 0),
            tax_rate         INTEGER NOT NULL DEFAULT 0 CHECK (tax_rate BETWEEN 0 AND 10000),
            tax_amount       INTEGER NOT NULL DEFAULT 0 CHECK (tax_amount >= 0),
            total_amount     INTEGER NOT NULL,
            payment_method   TEXT NOT NULL DEFAULT 'CASH' CHECK (payment_method IN ('CASH', 'BANK', 'CHEQUE', 'ONLINE', 'OTHER')),
            account_id       INTEGER NOT NULL REFERENCES money_accounts (id) ON DELETE RESTRICT,
            reference_number TEXT NOT NULL DEFAULT '',
            notes            TEXT NOT NULL DEFAULT '',
            status           TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'CANCELLED')),
            cancelled_at     TEXT,
            cancelled_on     TEXT CHECK (cancelled_on IS NULL OR cancelled_on {IsDate}),
            cancel_reason    TEXT NOT NULL DEFAULT '',
            created_by       INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
            cancelled_by     INTEGER REFERENCES users (id) ON DELETE RESTRICT,
            created_at       TEXT NOT NULL DEFAULT {Now},
            updated_at       TEXT NOT NULL DEFAULT {Now},
            CHECK (total_amount = amount + tax_amount),
            CHECK ((status = 'CANCELLED') = (cancelled_on IS NOT NULL))
        );
        CREATE INDEX expenses_category_date ON expenses (category_id, date);
        CREATE INDEX expenses_vendor_date ON expenses (vendor_id, date);
        CREATE INDEX expenses_account ON expenses (account_id, date);
        CREATE INDEX expenses_date ON expenses (date, id);
        CREATE TRIGGER expenses_no_delete BEFORE DELETE ON expenses
            BEGIN SELECT RAISE(ABORT, 'Expenses cannot be deleted; cancel them instead.'); END;
        CREATE TRIGGER expenses_cancel_is_final BEFORE UPDATE ON expenses WHEN OLD.status = 'CANCELLED'
            BEGIN SELECT RAISE(ABORT, 'A cancelled expense cannot be changed.'); END;
        CREATE TRIGGER expenses_amounts_fixed BEFORE UPDATE OF amount, tax_rate, tax_amount, total_amount, account_id, date ON expenses
            BEGIN SELECT RAISE(ABORT, 'Expense amounts cannot be changed; cancel and re-enter instead.'); END;

        CREATE TABLE contras (
            id               INTEGER PRIMARY KEY,
            number           TEXT NOT NULL UNIQUE,
            date             TEXT NOT NULL CHECK (date {IsDate}),
            from_account_id  INTEGER NOT NULL REFERENCES money_accounts (id) ON DELETE RESTRICT,
            to_account_id    INTEGER NOT NULL REFERENCES money_accounts (id) ON DELETE RESTRICT,
            amount           INTEGER NOT NULL CHECK (amount > 0),
            reference_number TEXT NOT NULL DEFAULT '',
            notes            TEXT NOT NULL DEFAULT '',
            status           TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'CANCELLED')),
            cancelled_at     TEXT,
            cancelled_on     TEXT CHECK (cancelled_on IS NULL OR cancelled_on {IsDate}),
            cancel_reason    TEXT NOT NULL DEFAULT '',
            created_by       INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
            cancelled_by     INTEGER REFERENCES users (id) ON DELETE RESTRICT,
            created_at       TEXT NOT NULL DEFAULT {Now},
            updated_at       TEXT NOT NULL DEFAULT {Now},
            CHECK (from_account_id <> to_account_id),
            CHECK ((status = 'CANCELLED') = (cancelled_on IS NOT NULL))
        );
        CREATE INDEX contras_from ON contras (from_account_id, date);
        CREATE INDEX contras_to ON contras (to_account_id, date);
        CREATE TRIGGER contras_no_delete BEFORE DELETE ON contras
            BEGIN SELECT RAISE(ABORT, 'Contra entries cannot be deleted; cancel them instead.'); END;
        CREATE TRIGGER contras_cancel_is_final BEFORE UPDATE ON contras WHEN OLD.status = 'CANCELLED'
            BEGIN SELECT RAISE(ABORT, 'A cancelled contra entry cannot be changed.'); END;

        CREATE TABLE ledger_accounts (
            id              INTEGER PRIMARY KEY,
            name            TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(trim(name)) > 0),
            "group"         TEXT NOT NULL CHECK ("group" IN ('CAPITAL', 'RESERVES', 'LOANS', 'CURRENT_LIABILITIES', 'DUTIES_TAXES',
                            'FIXED_ASSETS', 'INVESTMENTS', 'CURRENT_ASSETS', 'LOANS_ADVANCES', 'DIRECT_INCOME', 'INDIRECT_INCOME',
                            'DIRECT_EXPENSES', 'INDIRECT_EXPENSES')),
            opening_balance INTEGER NOT NULL DEFAULT 0 CHECK (opening_balance >= 0),
            opening_side    TEXT NOT NULL DEFAULT 'DR' CHECK (opening_side IN ('DR', 'CR')),
            notes           TEXT NOT NULL DEFAULT '',
            is_active       INTEGER NOT NULL DEFAULT 1,
            created_at      TEXT NOT NULL DEFAULT {Now},
            updated_at      TEXT NOT NULL DEFAULT {Now}
        );

        CREATE TABLE journal_vouchers (
            id            INTEGER PRIMARY KEY,
            number        TEXT NOT NULL UNIQUE,
            date          TEXT NOT NULL CHECK (date {IsDate}),
            narration     TEXT NOT NULL DEFAULT '',
            total_amount  INTEGER NOT NULL CHECK (total_amount > 0),
            status        TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'CANCELLED')),
            cancelled_at  TEXT,
            cancelled_on  TEXT CHECK (cancelled_on IS NULL OR cancelled_on {IsDate}),
            cancel_reason TEXT NOT NULL DEFAULT '',
            created_by    INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
            cancelled_by  INTEGER REFERENCES users (id) ON DELETE RESTRICT,
            created_at    TEXT NOT NULL DEFAULT {Now},
            updated_at    TEXT NOT NULL DEFAULT {Now},
            CHECK ((status = 'CANCELLED') = (cancelled_on IS NOT NULL))
        );
        CREATE INDEX journal_vouchers_date ON journal_vouchers (date, id);
        CREATE TRIGGER journal_vouchers_no_delete BEFORE DELETE ON journal_vouchers
            BEGIN SELECT RAISE(ABORT, 'Journal vouchers cannot be deleted; cancel them instead.'); END;
        CREATE TRIGGER journal_vouchers_cancel_is_final BEFORE UPDATE ON journal_vouchers WHEN OLD.status = 'CANCELLED'
            BEGIN SELECT RAISE(ABORT, 'A cancelled journal voucher cannot be changed.'); END;

        -- Each line posts to exactly one of: a chart-of-accounts ledger, a cash/bank account,
        -- a customer or vendor, or an expense category; and is either a debit or a credit.
        CREATE TABLE journal_lines (
            id                  INTEGER PRIMARY KEY,
            voucher_id          INTEGER NOT NULL REFERENCES journal_vouchers (id) ON DELETE RESTRICT,
            line_no             INTEGER NOT NULL,
            ledger_id           INTEGER REFERENCES ledger_accounts (id) ON DELETE RESTRICT,
            money_account_id    INTEGER REFERENCES money_accounts (id) ON DELETE RESTRICT,
            party_id            INTEGER REFERENCES parties (id) ON DELETE RESTRICT,
            expense_category_id INTEGER REFERENCES expense_categories (id) ON DELETE RESTRICT,
            debit               INTEGER NOT NULL DEFAULT 0 CHECK (debit >= 0),
            credit              INTEGER NOT NULL DEFAULT 0 CHECK (credit >= 0),
            narration           TEXT NOT NULL DEFAULT '',
            UNIQUE (voucher_id, line_no),
            CHECK ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0)),
            CHECK ((ledger_id IS NOT NULL) + (money_account_id IS NOT NULL) + (party_id IS NOT NULL) + (expense_category_id IS NOT NULL) = 1)
        );
        CREATE INDEX journal_lines_ledger ON journal_lines (ledger_id);
        CREATE INDEX journal_lines_money_account ON journal_lines (money_account_id);
        CREATE INDEX journal_lines_party ON journal_lines (party_id);
        CREATE INDEX journal_lines_expense_category ON journal_lines (expense_category_id);
        CREATE TRIGGER journal_lines_no_update BEFORE UPDATE ON journal_lines
            BEGIN SELECT RAISE(ABORT, 'Journal lines cannot be changed.'); END;
        CREATE TRIGGER journal_lines_no_delete BEFORE DELETE ON journal_lines
            BEGIN SELECT RAISE(ABORT, 'Journal lines cannot be deleted.'); END;
        """,

        // 4: first/last names (as the web version), and sign-in sessions for the built-in server
        $"""
        ALTER TABLE users ADD COLUMN first_name TEXT NOT NULL DEFAULT '';
        ALTER TABLE users ADD COLUMN last_name TEXT NOT NULL DEFAULT '';
        UPDATE users SET first_name = full_name;

        CREATE TABLE sessions (
            token      TEXT PRIMARY KEY,
            user_id    INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
            created_at TEXT NOT NULL DEFAULT {Now},
            expires_at TEXT NOT NULL,
            ip_address TEXT
        );
        CREATE INDEX sessions_user ON sessions (user_id);
        """,

        // 5: online copy for phones (Firebase): settings, phones allowed to read it, uploaded pages
        """
        CREATE TABLE cloud_state (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE TABLE cloud_devices (
            uid       TEXT PRIMARY KEY,
            user_id   INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
            name      TEXT NOT NULL DEFAULT '',
            me        TEXT NOT NULL,
            perms     TEXT NOT NULL,
            added_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        CREATE TABLE cloud_views (
            id    TEXT PRIMARY KEY,
            hash  TEXT NOT NULL,
            parts INTEGER NOT NULL
        );
        """,

        // 6: sales and receipts made on phones (own number series per phone, booked when the PC is online)
        """
        ALTER TABLE cloud_devices ADD COLUMN prefix TEXT NOT NULL DEFAULT '';
        CREATE TABLE cloud_inbox (
            number      TEXT PRIMARY KEY,
            uid         TEXT NOT NULL,
            user_id     INTEGER,
            kind        TEXT NOT NULL CHECK (kind IN ('sale', 'receipt')),
            body        TEXT NOT NULL,
            device      TEXT NOT NULL DEFAULT '',
            status      TEXT NOT NULL DEFAULT 'new' CHECK (status IN ('new', 'booked', 'failed')),
            message     TEXT NOT NULL DEFAULT '',
            doc_id      INTEGER,
            received_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            booked_at   TEXT
        );
        """,
    ];

    private const string Now = "(strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))";
    private const string DiscountTypeCheck = "CHECK (discount_type IS NULL OR discount_type IN ('PERCENTAGE', 'FIXED'))";

    /// <summary>
    /// Sales invoices and purchase bills share one column set. Documents are never deleted
    /// (they are cancelled) and their lines never change after saving.
    /// </summary>
    private static string TradeDocumentTables(string table, string itemsTable, string extraColumns) =>
        $"""
        CREATE TABLE {table} (
            id                  INTEGER PRIMARY KEY,
            number              TEXT NOT NULL UNIQUE,
            party_id            INTEGER NOT NULL REFERENCES parties (id) ON DELETE RESTRICT,
            {extraColumns}
            date                TEXT NOT NULL CHECK (date {IsDate}),
            due_date            TEXT NOT NULL CHECK (due_date {IsDate} AND due_date >= date),
            subtotal            INTEGER NOT NULL DEFAULT 0 CHECK (subtotal >= 0),
            item_discount_total INTEGER NOT NULL DEFAULT 0 CHECK (item_discount_total >= 0),
            discount_type       TEXT {DiscountTypeCheck},
            discount_value      INTEGER NOT NULL DEFAULT 0 CHECK (discount_value >= 0),
            discount_amount     INTEGER NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
            taxable_amount      INTEGER NOT NULL DEFAULT 0 CHECK (taxable_amount >= 0),
            tax_amount          INTEGER NOT NULL DEFAULT 0 CHECK (tax_amount >= 0),
            total_amount        INTEGER NOT NULL DEFAULT 0 CHECK (total_amount >= 0),
            amount_paid         INTEGER NOT NULL DEFAULT 0 CHECK (amount_paid >= 0 AND amount_paid <= total_amount),
            payment_status      TEXT NOT NULL DEFAULT 'UNPAID' CHECK (payment_status IN ('PAID', 'PARTIAL', 'UNPAID')),
            status              TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'CANCELLED')),
            notes               TEXT NOT NULL DEFAULT '',
            cancelled_at        TEXT,
            cancelled_on        TEXT CHECK (cancelled_on IS NULL OR cancelled_on {IsDate}),
            cancel_reason       TEXT NOT NULL DEFAULT '',
            created_by          INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
            cancelled_by        INTEGER REFERENCES users (id) ON DELETE RESTRICT,
            created_at          TEXT NOT NULL DEFAULT {Now},
            updated_at          TEXT NOT NULL DEFAULT {Now},
            CHECK ((status = 'CANCELLED') = (cancelled_on IS NOT NULL))
        );
        CREATE INDEX {table}_party_date ON {table} (party_id, date);
        CREATE INDEX {table}_date ON {table} (date, id);
        CREATE INDEX {table}_status_due ON {table} (status, payment_status, due_date);
        CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table}
            BEGIN SELECT RAISE(ABORT, 'Documents cannot be deleted; cancel them instead.'); END;
        CREATE TRIGGER {table}_cancel_is_final BEFORE UPDATE OF status ON {table} WHEN OLD.status = 'CANCELLED'
            BEGIN SELECT RAISE(ABORT, 'A cancelled document cannot be changed.'); END;

        CREATE TABLE {itemsTable} (
            id                     INTEGER PRIMARY KEY,
            document_id            INTEGER NOT NULL REFERENCES {table} (id) ON DELETE RESTRICT,
            product_id             INTEGER NOT NULL REFERENCES products (id) ON DELETE RESTRICT,
            quantity               INTEGER NOT NULL CHECK (quantity > 0),
            unit_price             INTEGER NOT NULL CHECK (unit_price >= 0),
            gross_amount           INTEGER NOT NULL CHECK (gross_amount >= 0),
            discount_type          TEXT {DiscountTypeCheck},
            discount_value         INTEGER NOT NULL DEFAULT 0 CHECK (discount_value >= 0),
            discount_amount        INTEGER NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
            invoice_discount_share INTEGER NOT NULL DEFAULT 0 CHECK (invoice_discount_share >= 0),
            taxable_amount         INTEGER NOT NULL CHECK (taxable_amount >= 0),
            tax_rate               INTEGER NOT NULL CHECK (tax_rate BETWEEN 0 AND 10000),
            tax_amount             INTEGER NOT NULL CHECK (tax_amount >= 0),
            line_total             INTEGER NOT NULL CHECK (line_total >= 0)
        );
        CREATE INDEX {itemsTable}_document ON {itemsTable} (document_id);
        CREATE INDEX {itemsTable}_product ON {itemsTable} (product_id);
        CREATE TRIGGER {itemsTable}_no_update BEFORE UPDATE ON {itemsTable}
            BEGIN SELECT RAISE(ABORT, 'Document lines cannot be changed.'); END;
        CREATE TRIGGER {itemsTable}_no_delete BEFORE DELETE ON {itemsTable}
            BEGIN SELECT RAISE(ABORT, 'Document lines cannot be deleted.'); END;

        """;

    /// <summary>
    /// Money received from customers / paid to vendors, and its allocation to invoices / bills.
    /// allocated_amount is a cache kept by the service layer; the CHECK makes over-allocation impossible.
    /// Allocations are never deleted: un-allocating marks them inactive with voided_on, so
    /// historical aging can reconstruct what was outstanding on any past date.
    /// </summary>
    private static string PaymentTables(string table, string allocationTable, string documentTable) =>
        $"""
        CREATE TABLE {table} (
            id               INTEGER PRIMARY KEY,
            number           TEXT NOT NULL UNIQUE,
            party_id         INTEGER NOT NULL REFERENCES parties (id) ON DELETE RESTRICT,
            date             TEXT NOT NULL CHECK (date {IsDate}),
            amount           INTEGER NOT NULL CHECK (amount > 0),
            allocated_amount INTEGER NOT NULL DEFAULT 0 CHECK (allocated_amount >= 0 AND allocated_amount <= amount),
            payment_method   TEXT NOT NULL DEFAULT 'CASH' CHECK (payment_method IN ('CASH', 'BANK', 'CHEQUE', 'ONLINE', 'OTHER')),
            account_id       INTEGER NOT NULL REFERENCES money_accounts (id) ON DELETE RESTRICT,
            reference_number TEXT NOT NULL DEFAULT '',
            notes            TEXT NOT NULL DEFAULT '',
            status           TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'CANCELLED')),
            cancelled_at     TEXT,
            cancelled_on     TEXT CHECK (cancelled_on IS NULL OR cancelled_on {IsDate}),
            cancel_reason    TEXT NOT NULL DEFAULT '',
            created_by       INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
            cancelled_by     INTEGER REFERENCES users (id) ON DELETE RESTRICT,
            created_at       TEXT NOT NULL DEFAULT {Now},
            updated_at       TEXT NOT NULL DEFAULT {Now},
            CHECK ((status = 'CANCELLED') = (cancelled_on IS NOT NULL))
        );
        CREATE INDEX {table}_party_date ON {table} (party_id, date);
        CREATE INDEX {table}_account ON {table} (account_id, date);
        CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table}
            BEGIN SELECT RAISE(ABORT, 'Payments cannot be deleted; cancel them instead.'); END;
        CREATE TRIGGER {table}_cancel_is_final BEFORE UPDATE OF status ON {table} WHEN OLD.status = 'CANCELLED'
            BEGIN SELECT RAISE(ABORT, 'A cancelled payment cannot be changed.'); END;

        CREATE TABLE {allocationTable} (
            id          INTEGER PRIMARY KEY,
            payment_id  INTEGER NOT NULL REFERENCES {table} (id) ON DELETE RESTRICT,
            document_id INTEGER NOT NULL REFERENCES {documentTable} (id) ON DELETE RESTRICT,
            amount      INTEGER NOT NULL CHECK (amount > 0),
            date        TEXT NOT NULL CHECK (date {IsDate}),
            is_active   INTEGER NOT NULL DEFAULT 1,
            created_at  TEXT NOT NULL DEFAULT {Now},
            created_by  INTEGER NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
            voided_at   TEXT,
            voided_on   TEXT CHECK (voided_on IS NULL OR voided_on {IsDate}),
            voided_by   INTEGER REFERENCES users (id) ON DELETE RESTRICT,
            void_reason TEXT NOT NULL DEFAULT '',
            CHECK (is_active = (voided_on IS NULL))
        );
        CREATE INDEX {allocationTable}_document ON {allocationTable} (document_id, is_active);
        CREATE INDEX {allocationTable}_payment ON {allocationTable} (payment_id, is_active);
        CREATE TRIGGER {allocationTable}_no_delete BEFORE DELETE ON {allocationTable}
            BEGIN SELECT RAISE(ABORT, 'Allocations cannot be deleted; void them instead.'); END;
        CREATE TRIGGER {allocationTable}_void_is_final BEFORE UPDATE ON {allocationTable} WHEN OLD.is_active = 0
            BEGIN SELECT RAISE(ABORT, 'A voided allocation cannot be changed.'); END;

        """;
}
