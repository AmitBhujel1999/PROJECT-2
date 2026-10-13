/**
 * Bikram Sambat (B.S.) <-> Gregorian (A.D.) conversion.
 *
 * Month lengths for B.S. 2000–2090 (A.D. 1943–2034), from the MIT-licensed
 * nepali-date-converter package. Each year is 12 digits; digit + 28 = days in
 * that month (Baisakh … Chaitra). B.S. 2000-01-01 is A.D. 1943-04-14.
 * The backend keeps the same table in apps/common/nepali_date.py.
 */
const TABLE =
	'243432221213,334333212122,334432212122,343432221123,243432221213,334333212122,334432212122,343432221123,333433122113,334333212122,334432212122,343432221123,333433122122,334333212122,334432212122,343432221123,333433122122,334333212122,343432212122,343432221213,333433212122,334333212122,343432221122,343432221213,333433212122,334333212122,343432221123,243432221213,334333212122,334342212122,343432221123,243432221213,334333212122,334432212122,343432221123,243433122113,334333212122,334432212122,343432221123,333433122122,334333212122,334432212122,343432221123,333433122122,334333212122,343432212122,343432221123,333433212122,334333212122,343432221122,343432221213,333433212122,334333212122,343432221122,343432221213,334333212122,334342212122,343432221123,243432221213,334333212122,334432212122,343432221123,243433121213,334333212122,334432212122,343432221123,333433122113,334333212122,334432212122,343432221123,333433122122,334333212122,343432212122,343432221123,333433212122,334333212122,343432221122,343432221213,333433212122,334333212122,343432221122,343432221213,334333212122,334333212122,343432221123,243432221213,334333212122,334333221222,234423221222,243432221222,243432221222';

export const BS_MIN_YEAR = 2000;
const MONTH_DAYS: number[][] = TABLE.split(',').map((row) => [...row].map((c) => Number(c) + 28));
export const BS_MAX_YEAR = BS_MIN_YEAR + MONTH_DAYS.length - 1;
const EPOCH_UTC = Date.UTC(1943, 3, 14);
const DAY_MS = 86_400_000;

export const BS_MONTHS = [
	'Baisakh', 'Jestha', 'Asar', 'Shrawan', 'Bhadra', 'Ashwin',
	'Kartik', 'Mangsir', 'Poush', 'Magh', 'Falgun', 'Chaitra'
];

export interface BsDate {
	year: number;
	month: number; // 1–12
	day: number;
}

export function daysInBsMonth(year: number, month: number): number {
	const row = MONTH_DAYS[year - BS_MIN_YEAR];
	if (!row || month < 1 || month > 12) throw new RangeError(`B.S. ${year}-${month} is outside the supported range`);
	return row[month - 1];
}

/** "2026-10-13" (A.D.) -> { year: 2083, month: 6, day: 27 } */
export function adToBs(iso: string): BsDate {
	const [y, m, d] = iso.slice(0, 10).split('-').map(Number);
	let days = Math.round((Date.UTC(y, m - 1, d) - EPOCH_UTC) / DAY_MS);
	if (days < 0) throw new RangeError(`${iso} is before B.S. ${BS_MIN_YEAR}`);
	for (let i = 0; i < MONTH_DAYS.length; i++) {
		for (let mi = 0; mi < 12; mi++) {
			const len = MONTH_DAYS[i][mi];
			if (days < len) return { year: BS_MIN_YEAR + i, month: mi + 1, day: days + 1 };
			days -= len;
		}
	}
	throw new RangeError(`${iso} is after B.S. ${BS_MAX_YEAR}`);
}

/** { year: 2083, month: 6, day: 27 } -> "2026-10-13" (A.D.) */
export function bsToAd(bs: BsDate): string {
	if (bs.day < 1 || bs.day > daysInBsMonth(bs.year, bs.month)) {
		throw new RangeError(`Invalid B.S. date ${bs.year}-${bs.month}-${bs.day}`);
	}
	let days = bs.day - 1;
	for (let i = 0; i < bs.year - BS_MIN_YEAR; i++) days += MONTH_DAYS[i].reduce((a, b) => a + b, 0);
	for (let mi = 0; mi < bs.month - 1; mi++) days += MONTH_DAYS[bs.year - BS_MIN_YEAR][mi];
	return new Date(EPOCH_UTC + days * DAY_MS).toISOString().slice(0, 10);
}

const pad = (n: number) => String(n).padStart(2, '0');

/** B.S. date as "2083-06-27" (the form typed into B.S. date fields). */
export function bsIso(bs: BsDate): string {
	return `${bs.year}-${pad(bs.month)}-${pad(bs.day)}`;
}

/**
 * Lenient parse for typed dates; missing parts come from `ref` (usually today):
 * "2083/06/27", "20830627", "83/6/27" (2083), "6/27" (this year), "27" (this month).
 */
export function parseBsInput(text: string, ref: BsDate): BsDate | null {
	const t = text.trim();
	const parts = /^\d{8}$/.test(t) ? [t.slice(0, 4), t.slice(4, 6), t.slice(6)] : t.split(/[-/.\s]+/);
	if (!parts.every((p) => /^\d{1,4}$/.test(p))) return null;
	const n = parts.map(Number);
	let bs: BsDate;
	if (parts.length === 1) bs = { year: ref.year, month: ref.month, day: n[0] };
	else if (parts.length === 2) bs = { year: ref.year, month: n[0], day: n[1] };
	else if (parts.length === 3) bs = { year: parts[0].length <= 2 ? 2000 + n[0] : n[0], month: n[1], day: n[2] };
	else return null;
	try {
		bsToAd(bs);
		return bs;
	} catch {
		return null;
	}
}

/** While typing digits only, add the slashes: "2083062" -> "2083/06/2". */
export function autoSlash(text: string): string {
	const digits = text.replace(/\//g, '');
	if (!/^\d{5,8}$/.test(digits) || !/^\d{4}\/?\d{0,2}\/?\d{0,2}$/.test(text)) return text;
	return [digits.slice(0, 4), digits.slice(4, 6), digits.slice(6)].filter(Boolean).join('/');
}

/** Parse "2083-06-27", "2083/6/27" or "2083.06.27"; null when not a valid B.S. date. */
export function parseBs(text: string): BsDate | null {
	const m = text.trim().match(/^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})$/);
	if (!m) return null;
	const bs = { year: Number(m[1]), month: Number(m[2]), day: Number(m[3]) };
	try {
		bsToAd(bs);
		return bs;
	} catch {
		return null;
	}
}
