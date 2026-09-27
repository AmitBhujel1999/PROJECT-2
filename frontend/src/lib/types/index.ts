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
	opening_stock: Qty;
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
	reference_type: 'document' | 'payment';
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
