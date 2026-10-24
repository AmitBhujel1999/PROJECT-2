/**
 * Sales and receipts made on a phone while it uses the online copy (away from the shop Wi-Fi).
 *
 * The phone works out the totals itself (calc.ts, same rules as the PC), gives the bill the next
 * number of its own series (M1-000001, receipts M1-R-000001), and leaves it in Firebase
 * (books/{book}/outbox/{number}). The PC books it when it is next online, keeping that number,
 * and writes back books/{book}/results/{number} (booked / failed). Until then the bill is shown
 * here as "Waiting for the computer". The number never changes: it is printed for the customer.
 */
import { ApiError } from '$lib/api/client';
import { calculate, fmt, scaled, type DiscountType, type Totals } from './calc';
import { currentBookCache as currentBook, cachedPage, cloudLink, currentToken, firestore, firestoreBase, loadBook } from './cloud';

type Kind = 'sale' | 'receipt';

export interface Pending {
	number: string;
	kind: Kind;
	created: string;
	status: 'waiting' | 'booked' | 'failed';
	message: string;
	realId: number;
	doc: any; // detail, shaped like the PC's answer
	row: any; // list row
}

// ---------------------------------------------------------------- the phone's waiting list

function key(): string {
	return `ml-pending-${cloudLink()?.book ?? ''}`;
}

export function pendingList(): Pending[] {
	try {
		return JSON.parse(localStorage.getItem(key()) ?? '[]') as Pending[];
	} catch {
		return [];
	}
}

function savePending(list: Pending[]) {
	try {
		localStorage.setItem(key(), JSON.stringify(list));
	} catch {
		// Storage full or blocked: the bill is safe in Firebase anyway.
	}
}

export function pendingById(id: string): Pending | undefined {
	return pendingList().find((p) => `p-${p.number}` === id);
}

let lastCheck = 0;

/** Ask Firebase what happened to bills still waiting (at most every 20 seconds). */
export async function refreshPending(force = false): Promise<Pending[]> {
	const list = pendingList();
	const link = cloudLink();
	if (!link || (!force && Date.now() - lastCheck < 20_000)) return list;
	lastCheck = Date.now();
	let changed = false;
	for (const p of list.filter((x) => x.status !== 'booked')) {
		const f = await firestore(`books/${link.book}/results/${p.number}`).catch(() => null);
		if (!f) continue;
		const status = f.status?.stringValue === 'booked' ? 'booked' : 'failed';
		p.status = status;
		p.message = f.message?.stringValue ?? '';
		p.realId = Number(f.id?.integerValue ?? 0);
		p.doc.pending_status = status;
		p.row.pending_status = status;
		changed = true;
	}
	// Booked bills are in the online copy itself after the next upload; keep them a day for reference.
	const kept = list.filter((p) => p.status !== 'booked' || Date.now() - new Date(p.created).getTime() < 86_400_000);
	if (changed || kept.length !== list.length) savePending(kept);
	return kept;
}

// ---------------------------------------------------------------- numbering

async function nextNumber(kind: Kind, body: any): Promise<string> {
	const b = await loadBook(true);
	const link = cloudLink();
	if (!b || !link) throw new ApiError(401, 'NOT_LINKED', 'This phone is not linked.');
	if (!b.entry) throw new ApiError(403, 'PHONE_ENTRY_OFF', 'Sales and receipts on phones are switched off on the computer.');
	if (!b.prefix) throw new ApiError(403, 'NO_SERIES', 'Link this phone again on the shop Wi-Fi to get its own bill numbers.');
	const counter = `ml-next-${link.book}-${kind}`;
	let n = Math.max(Number(localStorage.getItem(counter) ?? 0), kind === 'sale' ? b.nextSale : b.nextReceipt);
	for (let tries = 0; tries < 50; tries++, n++) {
		const number = `${b.prefix}-${kind === 'receipt' ? 'R-' : ''}${String(n).padStart(6, '0')}`;
		const r = await fetch(`${firestoreBase()}/books/${link.book}/outbox?documentId=${number}`, {
			method: 'POST',
			headers: { Authorization: `Bearer ${await currentToken()}`, 'Content-Type': 'application/json' },
			body: JSON.stringify({
				fields: {
					uid: { stringValue: link.uid },
					kind: { stringValue: kind },
					body: { stringValue: JSON.stringify(body) },
					device: { stringValue: navigator.userAgent.slice(0, 120) },
					created: { timestampValue: new Date().toISOString() }
				}
			})
		}).catch(() => null);
		if (!r) throw new ApiError(0, 'NETWORK_ERROR', 'No internet connection. The bill was not saved.');
		if (r.status === 409) continue; // number already used: take the next one
		if (r.status === 403) throw new ApiError(403, 'PERMISSION_DENIED', 'You are not allowed to do this from a phone.');
		if (!r.ok) throw new ApiError(r.status, 'CLOUD_ERROR', `Could not save (${r.status}). Try again.`);
		localStorage.setItem(counter, String(n + 1));
		return number;
	}
	throw new ApiError(409, 'NO_NUMBER', 'Could not find a free bill number. Open the books on the shop Wi-Fi.');
}

// ---------------------------------------------------------------- data from the online copy

async function rows(path: string): Promise<any[]> {
	const page = await cachedPage(path).catch(() => null);
	return page?.results ?? [];
}

function can(perm: string): boolean {
	return (currentBook()?.me?.permissions ?? []).includes(perm);
}

const money = (v: bigint) => fmt(v, 2);
const today = () => new Date(Date.now() - new Date().getTimezoneOffset() * 60_000).toISOString().slice(0, 10);

// ---------------------------------------------------------------- sales

interface SaleBody {
	party?: number;
	date: string;
	due_date?: string;
	discount_type?: string | null;
	discount_value?: string;
	notes?: string;
	items: { product: number; quantity: string; unit_price: string | null; discount_type: string | null; discount_value: string }[];
	payment?: { pay_in_full?: boolean; amount?: string; account?: number; reference_number?: string };
}

const dtype = (v: string | null | undefined): DiscountType => (v === 'PERCENTAGE' || v === 'FIXED' ? v : null);

async function saleTotals(body: SaleBody): Promise<{ totals: Totals; products: any[] }> {
	if (!body.items?.length) throw new ApiError(400, 'VALIDATION_ERROR', 'Items: Add at least one item.', { items: ['Add at least one item.'] });
	const all = await rows('products/');
	const products = body.items.map((i) => {
		const p = all.find((x) => x.id === Number(i.product));
		if (!p) throw new ApiError(400, 'PRODUCT_NOT_FOUND', 'A product on this bill is not in the online copy.');
		if (!p.is_active) throw new ApiError(400, 'PRODUCT_INACTIVE', `${p.name} is inactive.`);
		return p;
	});
	const totals = calculate(
		body.items.map((i, n) => ({
			quantity: i.quantity,
			unitPrice: i.unit_price ?? products[n].selling_price,
			taxRate: products[n].tax_rate,
			discountType: dtype(i.discount_type),
			discountValue: i.discount_value ?? '0'
		})),
		dtype(body.discount_type),
		body.discount_value ?? '0'
	);
	return { totals, products };
}

/** POST sales/calculate/ on the phone. */
export async function calculateSale(body: SaleBody) {
	const { totals: t, products } = await saleTotals(body);
	return {
		subtotal: money(t.subtotal),
		item_discount_total: money(t.itemDiscounts),
		net_subtotal: money(t.netSubtotal),
		discount_type: t.discountType,
		discount_value: money(t.discountValue / 10_000n),
		discount_amount: money(t.discount),
		total_discount: money(t.totalDiscount),
		taxable_amount: money(t.taxable),
		tax_amount: money(t.tax),
		total_amount: money(t.total),
		lines: t.lines.map((l, i) => ({
			quantity: fmt(l.quantity, 3),
			unit_price: money(l.unitPrice),
			gross_amount: money(l.gross),
			discount_type: l.discountType,
			discount_value: money(l.discountValue / 10_000n),
			discount_amount: money(l.discount),
			net_amount: money(l.net),
			invoice_discount_share: money(l.share),
			taxable_amount: money(l.taxable),
			tax_rate: fmt(l.taxRate, 2),
			tax_amount: money(l.tax),
			total: money(l.total),
			product: products[i].id,
			product_name: products[i].name,
			sku_code: products[i].sku_code,
			unit: products[i].unit_display,
			available_stock: products[i].current_stock
		}))
	};
}

/** POST sales/ on the phone: a numbered bill that waits for the computer. */
export async function createSale(body: SaleBody) {
	if (!can('sales.create')) throw new ApiError(403, 'PERMISSION_DENIED', 'You do not have permission to perform this action.');
	if (!body.party) throw new ApiError(400, 'VALIDATION_ERROR', 'Party: This field is required.', { party: ['This field is required.'] });
	const customers = await rows('customers/');
	const party = customers.find((c) => c.id === Number(body.party));
	if (!party) throw new ApiError(400, 'PARTY_NOT_FOUND', 'Customer not found in the online copy. New customers can only be added on the computer.');
	if (!party.is_active) throw new ApiError(400, 'PARTY_INACTIVE', `${party.name} is inactive.`);
	const { totals: t, products } = await saleTotals(body);

	// Stock as of the last upload, minus what this phone already sold that is not booked yet.
	const waiting = pendingList().filter((p) => p.kind === 'sale' && p.status !== 'booked');
	for (const p of products) {
		const wanted = body.items.filter((i) => Number(i.product) === p.id).reduce((a, i) => a + scaled(i.quantity, 3), 0n);
		const sold = waiting.flatMap((w) => w.doc.items).filter((i: any) => i.product === p.id).reduce((a: bigint, i: any) => a + scaled(i.quantity, 3), 0n);
		const available = scaled(p.current_stock ?? '0', 3) - sold;
		if (wanted > available)
			throw new ApiError(409, 'INSUFFICIENT_STOCK', `Insufficient stock for ${p.name}. Available: ${fmt(available, 3).replace(/\.?0+$/, '')} ${p.unit_display}, requested: ${fmt(wanted, 3).replace(/\.?0+$/, '')} ${p.unit_display}.`, { product_id: p.id });
	}

	let paid = 0n;
	if (body.payment?.pay_in_full) paid = t.total;
	else if (body.payment?.amount) {
		paid = scaled(body.payment.amount, 2);
		if (paid <= 0n) throw new ApiError(400, 'VALIDATION_ERROR', 'Amount: Enter the amount paid.', { amount: ['Enter the amount paid.'] });
		if (paid > t.total) throw new ApiError(400, 'OVERPAYMENT', `The amount paid cannot exceed the invoice total (${money(t.total)}).`);
	}
	const date = body.date || today();
	const due = body.due_date || new Date(new Date(date).getTime() + Number(party.credit_days ?? 0) * 86_400_000).toISOString().slice(0, 10);

	const number = await nextNumber('sale', body);
	const status = paid === 0n ? 'UNPAID' : paid >= t.total ? 'PAID' : 'PARTIAL';
	const row = {
		id: `p-${number}`, invoice_number: number, date, due_date: due, customer: party.id, customer_name: party.name,
		subtotal: money(t.subtotal), item_discount_total: money(t.itemDiscounts), discount_type: t.discountType,
		discount_value: money(t.discountValue / 10_000n), discount_amount: money(t.discount), total_discount: money(t.totalDiscount),
		taxable_amount: money(t.taxable), tax_amount: money(t.tax), total_amount: money(t.total), amount_paid: money(paid),
		balance_due: money(t.total - paid), payment_status: status, status: 'ACTIVE', item_count: t.lines.length,
		total_quantity: fmt(t.lines.reduce((a, l) => a + l.quantity, 0n), 3), created_at: new Date().toISOString(),
		pending_status: 'waiting'
	};
	const doc = {
		...row,
		items: t.lines.map((l, i) => ({
			id: i + 1, product: products[i].id, product_name: products[i].name, sku_code: products[i].sku_code, unit: products[i].unit_display,
			quantity: fmt(l.quantity, 3), unit_price: money(l.unitPrice), gross_amount: money(l.gross), discount_type: l.discountType,
			discount_value: money(l.discountValue / 10_000n), discount_amount: money(l.discount), invoice_discount_share: money(l.share),
			taxable_amount: money(l.taxable), tax_rate: fmt(l.taxRate, 2), tax_amount: money(l.tax), total_price: money(l.total)
		})),
		customer_pan_vat_no: party.pan_vat_no, customer_address: party.address, customer_phone: party.phone,
		notes: body.notes ?? '', created_by_name: currentBook()?.me?.username ?? '', allocations: [], warnings: []
	};
	savePending([{ number, kind: 'sale', created: new Date().toISOString(), status: 'waiting', message: '', realId: 0, doc, row }, ...pendingList()]);
	return { data: doc, message: `Invoice ${number} saved on this phone. It is booked when the computer is next online.` };
}

// ---------------------------------------------------------------- receipts

interface ReceiptBody {
	party?: number;
	date: string;
	amount: string;
	account?: number;
	reference_number?: string;
	notes?: string;
	allocations?: { document: number; amount: string }[];
	auto_allocate?: boolean;
}

/** GET customer-receipts/open-documents/?party= from the online copy. */
export async function openInvoices(party: number) {
	const sales = await rows('sales/');
	const now = Date.now();
	return sales
		.filter((s) => s.customer === party && s.status === 'ACTIVE' && Number(s.balance_due) > 0)
		.sort((a, b) => String(a.date).localeCompare(String(b.date)))
		.map((s) => ({
			id: s.id, number: s.invoice_number, date: s.date, due_date: s.due_date, total_amount: s.total_amount,
			amount_paid: s.amount_paid, balance_due: s.balance_due, payment_status: s.payment_status,
			days_overdue: Math.max(0, Math.floor((now - new Date(s.due_date).getTime()) / 86_400_000))
		}));
}

export async function createReceipt(body: ReceiptBody) {
	if (!can('receipts.create')) throw new ApiError(403, 'PERMISSION_DENIED', 'You do not have permission to perform this action.');
	if (!body.party) throw new ApiError(400, 'VALIDATION_ERROR', 'Party: This field is required.', { party: ['This field is required.'] });
	const party = (await rows('customers/')).find((c) => c.id === Number(body.party));
	if (!party) throw new ApiError(400, 'PARTY_NOT_FOUND', 'Customer not found in the online copy.');
	const amount = scaled(body.amount, 2);
	if (amount < 1n) throw new ApiError(400, 'VALIDATION_ERROR', 'Amount: Ensure this value is greater than or equal to 0.01.', { amount: ['Ensure this value is greater than or equal to 0.01.'] });
	const open = await openInvoices(party.id);
	const allocations = (body.allocations ?? []).map((a) => {
		const inv = open.find((o) => o.id === Number(a.document));
		if (!inv) throw new ApiError(400, 'INVALID_ALLOCATION', 'An invoice in this receipt is not open any more.');
		const amt = scaled(a.amount, 2);
		if (amt > scaled(inv.balance_due, 2)) throw new ApiError(400, 'INVALID_ALLOCATION', `${inv.number}: more than its balance due (${inv.balance_due}).`);
		return { inv, amt };
	});
	const allocated = allocations.reduce((a, x) => a + x.amt, 0n);
	if (allocated > amount) throw new ApiError(400, 'INVALID_ALLOCATION', 'The allocations are more than the amount received.');
	const accounts = await rows('cash-bank/accounts/');
	const account = accounts.find((a) => a.id === Number(body.account)) ?? accounts.find((a) => a.kind === 'CASH') ?? accounts[0];

	const number = await nextNumber('receipt', body);
	const row = {
		id: `p-${number}`, number, receipt_number: number, party: party.id, party_name: party.name, date: body.date || today(),
		amount: money(amount), allocated_amount: money(allocated), unallocated_amount: money(amount - allocated),
		payment_method: account?.kind ?? 'CASH', payment_method_display: account?.name ?? 'Cash', account: account?.id ?? null,
		account_name: account?.name ?? '', reference_number: body.reference_number ?? '', notes: body.notes ?? '', status: 'ACTIVE',
		created_at: new Date().toISOString(), created_by_name: currentBook()?.me?.username ?? '', pending_status: 'waiting'
	};
	const doc = {
		...row, party_pan_vat_no: party.pan_vat_no, party_address: party.address,
		allocations: allocations.map((a, i) => ({
			id: i + 1, document_id: a.inv.id, document_number: a.inv.number, document_date: a.inv.date, document_total: a.inv.total_amount,
			amount: money(a.amt), date: row.date, is_active: true
		}))
	};
	savePending([{ number, kind: 'receipt', created: new Date().toISOString(), status: 'waiting', message: '', realId: 0, doc, row }, ...pendingList()]);
	const note = body.auto_allocate && !allocations.length ? ' It is matched to open invoices when booked.' : '';
	return { data: doc, message: `Receipt ${number} saved on this phone. It is booked when the computer is next online.${note}` };
}
