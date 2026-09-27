/**
 * Display formatting only. Values are exact decimal strings from the API;
 * no accounting is computed in the browser.
 */

const moneyFmt = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const qtyFmt = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 });

/** Format a decimal string with lakh/crore grouping (Nepal convention) without float drift. */
export function money(value: string | number | null | undefined, blankZero = false): string {
	if (value === null || value === undefined || value === '') return '';
	const str = String(value);
	const negative = str.startsWith('-');
	const [intPart, frac = ''] = str.replace('-', '').split('.');
	const cents = (frac + '00').slice(0, 2);
	if (blankZero && /^0*$/.test(intPart) && cents === '00') return '-';
	const grouped = groupIndian(intPart.replace(/^0+(?=\d)/, ''));
	return `${negative ? '-' : ''}${grouped}.${cents}`;
}

function groupIndian(digits: string): string {
	if (digits.length <= 3) return digits;
	const last3 = digits.slice(-3);
	const rest = digits.slice(0, -3).replace(/\B(?=(\d{2})+(?!\d))/g, ',');
	return `${rest},${last3}`;
}

export function qty(value: string | number | null | undefined): string {
	if (value === null || value === undefined || value === '') return '';
	const n = Number(value);
	return Number.isFinite(n) ? qtyFmt.format(n) : String(value);
}

export function currency(value: string | number | null | undefined, code = 'NPR'): string {
	return `${code} ${money(value)}`;
}

/** dd/mm/yyyy */
export function date(value: string | null | undefined): string {
	if (!value) return '';
	const [y, m, d] = value.slice(0, 10).split('-');
	return `${d}/${m}/${y}`;
}

export function dateTime(value: string | null | undefined): string {
	if (!value) return '';
	const d = new Date(value);
	return d.toLocaleString('en-GB', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}

export function today(): string {
	const d = new Date();
	return toIso(d);
}

export function toIso(d: Date): string {
	const pad = (n: number) => String(n).padStart(2, '0');
	return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export function addDays(iso: string, days: number): string {
	const [y, m, d] = iso.split('-').map(Number);
	const dt = new Date(y, m - 1, d);
	dt.setDate(dt.getDate() + days);
	return toIso(dt);
}

export function firstOfMonth(iso = today()): string {
	return `${iso.slice(0, 8)}01`;
}

export function isNegative(value: string | null | undefined): boolean {
	return !!value && value.trim().startsWith('-');
}

export function isZero(value: string | null | undefined): boolean {
	return !value || Number(value) === 0;
}

export const PAYMENT_STATUS_LABEL: Record<string, string> = { PAID: 'Paid', PARTIAL: 'Partial', UNPAID: 'Unpaid' };
export const STOCK_STATUS_LABEL: Record<string, string> = {
	IN_STOCK: 'In stock',
	LOW_STOCK: 'Low stock',
	OUT_OF_STOCK: 'Out of stock'
};
export const PAYMENT_METHODS = [
	{ value: 'CASH', label: 'Cash' },
	{ value: 'BANK', label: 'Bank' },
	{ value: 'CHEQUE', label: 'Cheque' },
	{ value: 'ONLINE', label: 'Online Transfer' },
	{ value: 'OTHER', label: 'Other' }
];
export const CREDIT_TERMS = [
	{ value: 'CASH', label: 'Cash' },
	{ value: 'DAYS_7', label: '7 Days' },
	{ value: 'DAYS_15', label: '15 Days' },
	{ value: 'DAYS_30', label: '30 Days' },
	{ value: 'DAYS_45', label: '45 Days' },
	{ value: 'DAYS_60', label: '60 Days' },
	{ value: 'DAYS_90', label: '90 Days' },
	{ value: 'CUSTOM', label: 'Custom' }
];
export const UNITS = [
	{ value: 'PCS', label: 'Pcs' },
	{ value: 'KG', label: 'Kg' },
	{ value: 'BOX', label: 'Box' },
	{ value: 'LTR', label: 'Ltr' },
	{ value: 'METER', label: 'Meter' },
	{ value: 'SET', label: 'Set' },
	{ value: 'PACK', label: 'Pack' },
	{ value: 'DOZEN', label: 'Dozen' }
];
export const ADJUSTMENT_REASONS = [
	{ value: 'DAMAGED', label: 'Damaged goods' },
	{ value: 'EXPIRED', label: 'Expired' },
	{ value: 'LOST', label: 'Lost / theft' },
	{ value: 'COUNT_CORRECTION', label: 'Physical count correction' },
	{ value: 'FOUND', label: 'Found / surplus' },
	{ value: 'RETURN', label: 'Returned goods' },
	{ value: 'OTHER', label: 'Other' }
];
