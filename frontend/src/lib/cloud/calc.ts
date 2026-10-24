/**
 * Invoice totals on the phone (online copy), exactly as the PC calculates them
 * (desktop/src/Accounting.Core/Calculations.cs): integer paisa, rounding half away from zero.
 *
 *   Quantity x Unit Price            -> Gross Amount   (per line, rounded)
 *   Gross Amount - Item Discount     -> Net Item Amount
 *   Invoice discount                 -> allocated to lines pro-rata by net amount (remainder to the largest)
 *   Net - share                      -> Taxable Amount; x line tax rate -> Tax Amount; + -> Line Total
 */
import { ApiError } from '$lib/api/client';

export type DiscountType = 'PERCENTAGE' | 'FIXED' | null;

export interface LineIn {
	quantity: string | number;
	unitPrice: string | number;
	taxRate: string | number;
	discountType: DiscountType;
	discountValue: string | number;
}

export interface LineOut {
	quantity: bigint; // thousandths
	unitPrice: bigint; // paisa
	gross: bigint;
	discountType: DiscountType;
	discountValue: bigint; // millionths (percent or rupees)
	discount: bigint;
	net: bigint;
	share: bigint;
	taxable: bigint;
	taxRate: bigint; // hundredths of a percent
	tax: bigint;
	total: bigint;
}

export interface Totals {
	lines: LineOut[];
	subtotal: bigint;
	itemDiscounts: bigint;
	netSubtotal: bigint;
	discountType: DiscountType;
	discountValue: bigint;
	discount: bigint;
	totalDiscount: bigint;
	taxable: bigint;
	tax: bigint;
	total: bigint;
}

/** Exact decimal text -> integer scaled by 10^scale, rounded half away from zero. */
export function scaled(value: string | number | null | undefined, scale: number): bigint {
	let text = String(value ?? '0').trim() || '0';
	if (/e/i.test(text)) text = Number(text).toFixed(12);
	const m = text.match(/^([+-]?)(\d*)(?:\.(\d*))?$/);
	if (!m) throw new ApiError(400, 'VALIDATION_ERROR', `"${value}" is not a number.`);
	const negative = m[1] === '-';
	const whole = m[2] || '0';
	const frac = (m[3] ?? '').padEnd(scale + 1, '0');
	let n = BigInt(whole + frac.slice(0, scale));
	if (Number(frac[scale]) >= 5) n += 1n;
	return negative ? -n : n;
}

/** a / b rounded half away from zero. */
export function roundDiv(a: bigint, b: bigint): bigint {
	const negative = a < 0n !== b < 0n;
	const x = a < 0n ? -a : a;
	const y = b < 0n ? -b : b;
	const q = (x * 2n + y) / (2n * y);
	return negative ? -q : q;
}

/** Integer scaled by 10^scale -> "1234.50". */
export function fmt(n: bigint, scale = 2): string {
	const negative = n < 0n;
	const s = (negative ? -n : n).toString().padStart(scale + 1, '0');
	return (negative ? '-' : '') + (scale ? `${s.slice(0, -scale)}.${s.slice(-scale)}` : s);
}

const fail = (message: string, code = 'INVALID_DISCOUNT') => new ApiError(400, code, message);

function discountOn(base: bigint, type: DiscountType, value: bigint, label: string): bigint {
	if (!type || value === 0n) return 0n;
	if (value < 0n) throw fail(`${label} cannot be negative.`);
	if (type === 'PERCENTAGE') {
		if (value > 100_000_000n) throw fail(`${label} percentage cannot exceed 100%.`);
		return roundDiv(base * value, 100_000_000n);
	}
	const amount = roundDiv(value, 10_000n);
	if (amount > base) throw fail(`${label} (${fmt(amount)}) cannot exceed the amount it applies to (${fmt(base)}).`);
	return amount;
}

export function calculate(lines: LineIn[], discountType: DiscountType, discountValue: string | number): Totals {
	if (!lines.length) throw fail('At least one item is required.', 'NO_ITEMS');
	const out: LineOut[] = lines.map((l, i) => {
		const label = `Line ${i + 1}`;
		const quantity = scaled(l.quantity, 3);
		const unitPrice = scaled(l.unitPrice, 2);
		const taxRate = scaled(l.taxRate, 2);
		if (quantity <= 0n) throw fail(`${label}: quantity must be greater than zero.`, 'INVALID_QUANTITY');
		if (unitPrice < 0n) throw fail(`${label}: price cannot be negative.`, 'INVALID_PRICE');
		if (taxRate < 0n || taxRate > 10_000n) throw fail(`${label}: tax rate must be between 0 and 100.`, 'INVALID_TAX_RATE');
		const gross = roundDiv(quantity * unitPrice, 1000n);
		const value = scaled(l.discountValue, 6);
		const discount = discountOn(gross, l.discountType, value, `${label} discount`);
		const type = discount > 0n || value > 0n ? l.discountType : null;
		return {
			quantity, unitPrice, gross, discountType: type, discountValue: type ? value : 0n, discount,
			net: gross - discount, share: 0n, taxable: gross - discount, taxRate, tax: 0n, total: 0n
		};
	});
	const sum = (f: (l: LineOut) => bigint) => out.reduce((a, l) => a + f(l), 0n);
	const subtotal = sum((l) => l.gross);
	const itemDiscounts = sum((l) => l.discount);
	const netSubtotal = subtotal - itemDiscounts;
	const value = scaled(discountValue, 6);
	const invoiceDiscount = discountOn(netSubtotal, discountType, value, 'Invoice discount');

	const netTotal = sum((l) => l.net);
	if (invoiceDiscount > 0n && netTotal > 0n) {
		let allocated = 0n;
		for (const l of out) {
			l.share = roundDiv(invoiceDiscount * l.net, netTotal);
			allocated += l.share;
		}
		const remainder = invoiceDiscount - allocated;
		if (remainder !== 0n) out.reduce((max, l) => (l.net > max.net ? l : max), out[0]).share += remainder;
		if (out.some((l) => l.share > l.net)) throw fail('Invoice discount allocation exceeded a line amount.');
	}
	for (const l of out) {
		l.taxable = l.net - l.share;
		l.tax = roundDiv(l.taxable * l.taxRate, 10_000n);
		l.total = l.taxable + l.tax;
	}
	const has = !!discountType && value > 0n;
	return {
		lines: out,
		subtotal,
		itemDiscounts,
		netSubtotal,
		discountType: has ? discountType : null,
		discountValue: has ? value : 0n,
		discount: invoiceDiscount,
		totalDiscount: itemDiscounts + invoiceDiscount,
		taxable: sum((l) => l.taxable),
		tax: sum((l) => l.tax),
		total: sum((l) => l.taxable) + sum((l) => l.tax)
	};
}
