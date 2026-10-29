// Money and quantities arrive from the API as exact decimal strings.
export type Money = string;
export type Qty = string;

export interface Paginated<T, Extra = Record<string, unknown>> {
	results: T[];
	count: number;
	page: number;
	page_size: number;
	total_pages: number;
	summary?: Extra;
	[key: string]: unknown;
}

export type Role = 'ADMIN' | 'MANAGER' | 'ACCOUNTANT' | 'STAFF';

export interface User {
	id: number;
	username: string;
	email: string;
	first_name: string;
	last_name: string;
	phone: string;
	role: Role;
	is_active: boolean;
	last_login: string | null;
	date_joined: string;
	display_name: string;
	permissions?: string[];
}

export type DiscountType = 'PERCENTAGE' | 'FIXED' | null;
export type PaymentStatus = 'PAID' | 'PARTIAL' | 'UNPAID';
export type DocStatus = 'ACTIVE' | 'CANCELLED';
export type PartyType = 'CUSTOMER' | 'VENDOR';

export interface Product {
	id: number;
	name: string;
	sku_code: string;
	unit: string;
	unit_display: string;
	description: string;
	hs_code: string;
	opening_stock: Qty;
	opening_rate: Money;
	opening_amount: Money;
	reorder_level: Qty;
	purchase_price: Money;
	selling_price: Money;
	tax_rate: string;
	is_active: boolean;
	current_stock: Qty | null;
	created_at: string;
	updated_at: string;
}

export interface Party {
	id: number;
	type: PartyType;
	type_display: string;
	name: string;
	pan_vat_no: string;
	phone: string;
	email: string;
	address: string;
	credit_terms: string;
	credit_terms_display: string;
	credit_days: number;
	credit_limit: Money;
	opening_balance: Money;
	opening_side: 'DR' | 'CR';
	notes: string;
	is_active: boolean;
	created_at: string;
}

export interface DocLine {
	id?: number;
	product: number;
	product_name: string;
	sku_code: string;
	unit: string;
	quantity: Qty;
	unit_price?: Money;
	unit_cost?: Money;
	gross_amount: Money;
	discount_type: DiscountType;
	discount_value: Money;
	discount_amount: Money;
	invoice_discount_share: Money;
	taxable_amount: Money;
	tax_rate: string;
	tax_amount: Money;
	total_price?: Money;
	total_cost?: Money;
	total?: Money;
	net_amount?: Money;
	available_stock?: Qty;
}

export interface TradeDoc {
	/** A bill made on a phone (online copy) that the computer has not booked yet. */
	pending_status?: 'waiting' | 'booked' | 'failed';
	pending_message?: string;
	id: number;
	invoice_number?: string;
	bill_number?: string;
	vendor_bill_number?: string;
	date: string;
	due_date: string;
	customer?: number;
	customer_name?: string;
	vendor?: number;
	vendor_name?: string;
	subtotal: Money;
	item_discount_total: Money;
	discount_type: DiscountType;
	discount_value: Money;
	discount_amount: Money;
	total_discount: Money;
	taxable_amount: Money;
	tax_amount: Money;
	total_amount: Money;
	amount_paid: Money;
	balance_due: Money;
	payment_status: PaymentStatus;
	status: DocStatus;
	item_count?: number;
	total_quantity?: Qty;
	items?: DocLine[];
	notes?: string;
	created_by_name?: string;
	cancelled_at?: string | null;
	cancel_reason?: string;
	warnings?: string[];
	allocations?: { id: number; receipt_number?: string; payment_number?: string; receipt_id?: number; payment_id?: number; date: string; amount: Money; is_active: boolean }[];
	[key: string]: unknown;
}

export interface Totals {
	subtotal: Money;
	item_discount_total: Money;
	net_subtotal: Money;
	discount_type: DiscountType;
	discount_value: Money;
	discount_amount: Money;
	total_discount: Money;
	taxable_amount: Money;
	tax_amount: Money;
	total_amount: Money;
	lines: DocLine[];
}

export interface PaymentDoc {
	/** A bill made on a phone (online copy) that the computer has not booked yet. */
	pending_status?: 'waiting' | 'booked' | 'failed';
	pending_message?: string;
	id: number;
	number: string;
	party: number;
	party_name: string;
	date: string;
	amount: Money;
	allocated_amount: Money;
	unallocated_amount: Money;
	payment_method: string;
	payment_method_display: string;
	account: number;
	account_name: string;
	reference_number: string;
	notes: string;
	status: DocStatus;
	created_at: string;
	created_by_name: string | null;
	cancelled_at?: string | null;
	cancel_reason?: string;
	party_pan_vat_no?: string;
	party_address?: string;
	allocations?: {
		id: number;
		document_id: number;
		document_number: string;
		document_date: string;
		document_total: Money;
		amount: Money;
		date: string;
		is_active: boolean;
		voided_at: string | null;
		void_reason: string;
	}[];
}

export interface OpenDocument {
	id: number;
	number: string;
	date: string;
	due_date: string;
	total_amount: Money;
	amount_paid: Money;
	balance_due: Money;
	payment_status: PaymentStatus;
	days_overdue: number;
}

export interface LedgerEntry {
	date: string;
	reference: string;
	reference_id: number;
	reference_type: 'document' | 'payment' | 'journal';
	transaction_type: string;
	description: string;
	debit: Money;
	credit: Money;
	balance: Money;
	due_date: string | null;
	payment_status: PaymentStatus | null;
	status: DocStatus;
}

export interface PartyHeader {
	id: number;
	name: string;
	type: PartyType;
	pan_vat_no: string;
	phone: string;
	email: string;
	address: string;
	credit_terms: string;
	credit_days: number;
	credit_limit: Money;
}

export interface AgingBucket {
	key: string;
	label: string;
}

export interface AgingRow {
	party_id: number;
	party_name: string;
	phone: string;
	pan_vat_no: string;
	current: Money;
	days_1_30: Money;
	days_31_60: Money;
	days_61_90: Money;
	days_91_120: Money;
	days_over_120: Money;
	total: Money;
	unallocated: Money;
	net_balance: Money;
	document_count: number;
	[key: string]: unknown;
}

export interface BusinessSettings {
	business_name: string;
	address: string;
	pan_vat_no: string;
	phone: string;
	email: string;
	currency_code: string;
	currency_symbol: string;
	tax_label: string;
	default_tax_rate: string;
	invoice_footer: string;
	/** '' until an admin chooses on first login. */
	date_system: '' | 'AD' | 'BS';
}

export interface StockRow {
	id: number;
	sku_code: string;
	name: string;
	unit: string;
	unit_display?: string;
	opening_stock: Qty;
	total_purchased: Qty;
	total_sold: Qty;
	total_adjusted: Qty;
	current_stock: Qty;
	purchase_price: Money;
	selling_price: Money;
	cost_value: Money;
	retail_value: Money;
	reorder_level: Qty;
	stock_status: 'IN_STOCK' | 'LOW_STOCK' | 'OUT_OF_STOCK';
	is_active: boolean;
}

export interface StockLedgerRow {
	id: number;
	date: string;
	product_id: number;
	product_name: string;
	sku_code: string;
	unit: string;
	transaction_type: string;
	transaction_type_display: string;
	reference_type: string;
	reference_id: number | null;
	reference_number: string;
	opening_quantity: Qty;
	quantity_in: Qty;
	quantity_out: Qty;
	closing_quantity: Qty;
	notes: string;
	created_by: string | null;
}

export interface ExpenseCategory {
	id: number;
	name: string;
	description: string;
	is_active: boolean;
	expense_count: number | null;
	created_at: string;
	updated_at: string;
}

export interface Expense {
	id: number;
	expense_number: string;
	date: string;
	category: number;
	category_name: string;
	vendor: number | null;
	vendor_name: string | null;
	payee: string;
	paid_to: string;
	description: string;
	amount: Money;
	tax_rate: string;
	tax_amount: Money;
	total_amount: Money;
	payment_method: string;
	payment_method_display: string;
	account: number;
	account_name: string;
	reference_number: string;
	status: DocStatus;
	created_at: string;
	notes?: string;
	created_by_name?: string | null;
	cancelled_at?: string | null;
	cancelled_by_name?: string | null;
	cancel_reason?: string;
	updated_at?: string;
}

export type AccountKind = 'CASH' | 'BANK' | 'WALLET' | 'OTHER';

/** A cash/bank account the business keeps money in. */
export interface MoneyAccount {
	id: number;
	name: string;
	kind: AccountKind;
	kind_display: string;
	account_number: string;
	opening_balance: string;
	notes: string;
	is_active: boolean;
	/** null when the user may not see balances. */
	balance: string | null;
}

export interface AccountBookEntry {
	date: string;
	voucher_type: 'Receipt' | 'Payment' | 'Expense' | 'Contra' | 'Journal';
	number: string;
	object_id: number;
	particulars: string;
	reference_number: string;
	money_in: string;
	money_out: string;
	balance: string;
}

export interface AccountBook {
	account: number;
	account_name: string;
	opening_balance: string;
	total_in: string;
	total_out: string;
	closing_balance: string;
	start_date: string | null;
	end_date: string;
	entries: AccountBookEntry[];
}

export interface Contra {
	id: number;
	contra_number: string;
	date: string;
	from_account: number;
	from_account_name: string;
	to_account: number;
	to_account_name: string;
	amount: string;
	reference_number: string;
	notes: string;
	status: DocStatus;
	cancelled_at: string | null;
	cancel_reason: string;
	created_at: string;
	created_by_name: string | null;
	cancelled_by_name: string | null;
}

export interface DayBookEntry {
	date: string;
	voucher_type: string;
	number: string;
	object_id: number;
	link: string;
	particulars: string;
	account_name: string;
	amount: string;
	status: DocStatus;
}

export interface DayBook {
	start_date: string;
	end_date: string;
	count: number;
	totals: { voucher_type: string; amount: string }[];
	entries: DayBookEntry[];
}

export type DrCr = { amount: string; side: 'Dr' | 'Cr' };

/** Chart-of-accounts ledger. */
export interface LedgerAccount {
	id: number;
	name: string;
	group: string;
	group_display: string;
	nature: 'ASSET' | 'LIABILITY' | 'INCOME' | 'EXPENSE';
	opening_balance: string;
	opening_side: 'DR' | 'CR';
	notes: string;
	is_active: boolean;
	balance: DrCr | null;
}

export interface AccountGroupOption {
	value: string;
	label: string;
	nature: LedgerAccount['nature'];
}

/** Anything a journal line can post to: "ledger:5", "cash:2", "party:9", "expense:3". */
export interface JournalAccountOption {
	key: string;
	name: string;
	group: string;
}

export interface JournalVoucher {
	id: number;
	voucher_number: string;
	date: string;
	narration: string;
	total_amount: string;
	status: DocStatus;
	cancelled_at: string | null;
	cancel_reason: string;
	created_at: string;
	created_by_name: string | null;
	cancelled_by_name: string | null;
	lines: { line_no: number; account: JournalAccountOption; debit: string; credit: string; narration: string }[];
}

export interface JournalVoucherRow {
	id: number;
	voucher_number: string;
	date: string;
	narration: string;
	total_amount: string;
	status: DocStatus;
	accounts: { debit: string[]; credit: string[] };
}

export interface LedgerBook {
	ledger: number;
	ledger_name: string;
	group_display: string;
	opening: DrCr;
	total_debit: string;
	total_credit: string;
	closing: DrCr;
	start_date: string | null;
	end_date: string;
	entries: { date: string; voucher_id: number; number: string; narration: string; debit: string; credit: string; balance: DrCr }[];
}

export interface StatementRow {
	id?: number | null;
	name: string;
	amount: string;
	link?: string;
}

export interface ProfitLoss {
	start_date: string | null;
	end_date: string;
	sales: string;
	direct_income: StatementRow[];
	opening_stock: string;
	opening_stock_added: string;
	purchases: string;
	closing_stock: string;
	cost_of_goods_sold: string;
	direct_expenses: StatementRow[];
	gross_profit: string;
	indirect_income: StatementRow[];
	expense_categories: StatementRow[];
	indirect_expenses: StatementRow[];
	total_indirect_expenses: string;
	net_profit: string;
}

export interface StatementGroup {
	key: string;
	label: string;
	rows: StatementRow[];
	total: string;
}

export interface BalanceSheet {
	as_of: string;
	liabilities: StatementGroup[];
	assets: StatementGroup[];
	total_liabilities: string;
	total_assets: string;
	difference: string;
	net_profit: string;
}

export interface CashFlow {
	start_date: string;
	end_date: string;
	opening_cash: string;
	operating: StatementRow[];
	investing: StatementRow[];
	financing: StatementRow[];
	net_operating: string;
	net_investing: string;
	net_financing: string;
	net_change: string;
	closing_cash: string;
	difference: string;
	accounts: StatementRow[];
}

export interface TrialBalanceRow {
	id?: number | null;
	name: string;
	debit: string;
	credit: string;
	link?: string;
}

export interface TrialBalanceGroup {
	key: string;
	label: string;
	rows: TrialBalanceRow[];
	debit: string;
	credit: string;
	count: number;
}

export interface TrialBalance {
	start_date: string | null;
	end_date: string;
	groups: TrialBalanceGroup[];
	total_debit: string;
	total_credit: string;
	difference: string;
}

export type OpeningSection = 'cash' | 'ledgers' | 'customers' | 'vendors' | 'stock';

export interface OpeningRow {
	id: number;
	name: string;
	is_active: boolean;
	opening_balance: string;
	opening_side: 'DR' | 'CR';
	kind?: string;
	group?: string;
	group_display?: string;
	phone?: string;
}

export interface OpeningStockRow {
	id: number;
	name: string;
	sku_code: string;
	unit: string;
	is_active: boolean;
	opening_stock: string;
	opening_rate: string;
	opening_amount: string;
}

export interface DebitCredit {
	debit: string;
	credit: string;
}

export interface OpeningBalances {
	cash: OpeningRow[];
	ledgers: OpeningRow[];
	customers: OpeningRow[];
	vendors: OpeningRow[];
	stock: OpeningStockRow[];
	totals: Record<OpeningSection, DebitCredit>;
	total_debit: string;
	total_credit: string;
	difference: DebitCredit;
	can_edit: Record<OpeningSection, boolean>;
}
