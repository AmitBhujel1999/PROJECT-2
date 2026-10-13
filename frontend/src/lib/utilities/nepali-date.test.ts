import { afterEach, describe, expect, it } from 'vitest';
import { adToBs, bsToAd, parseBs, daysInBsMonth, BS_MAX_YEAR } from './nepali-date';
import { date, dateTime, firstOfMonth, shortDate } from './format';
import { calendar } from '$lib/stores/calendar.svelte';
import { nepaliFiscalYear } from '$lib/components/layout/menu';

describe('nepali-date', () => {
	it.each([
		['1943-04-14', { year: 2000, month: 1, day: 1 }],
		['2026-10-13', { year: 2083, month: 6, day: 27 }],
		['2026-07-17', { year: 2083, month: 4, day: 1 }],
		['2024-04-13', { year: 2081, month: 1, day: 1 }]
	])('%s <-> B.S.', (ad, bs) => {
		expect(adToBs(ad)).toEqual(bs);
		expect(bsToAd(bs)).toBe(ad);
	});

	it('round-trips every day in range', () => {
		let t = Date.UTC(1943, 3, 14);
		for (;;) {
			const iso = new Date(t).toISOString().slice(0, 10);
			let bs;
			try {
				bs = adToBs(iso);
			} catch {
				break;
			}
			expect(bsToAd(bs)).toBe(iso);
			t += 86_400_000;
		}
		expect(adToBs(new Date(t - 86_400_000).toISOString().slice(0, 10)).year).toBe(BS_MAX_YEAR);
	});

	it('parses typed B.S. dates', () => {
		expect(parseBs('2083/06/27')).toEqual({ year: 2083, month: 6, day: 27 });
		expect(parseBs(' 2083-6-27 ')).toEqual({ year: 2083, month: 6, day: 27 });
		expect(parseBs('2083/06/32')).toBeNull();
		expect(parseBs('27/06/2083')).toBeNull();
		expect(daysInBsMonth(2083, 6)).toBe(31);
	});
});

describe('format in B.S. mode', () => {
	afterEach(() => {
		calendar.system = 'AD';
	});

	it('switches display with the calendar', () => {
		expect(date('2026-10-13')).toBe('13/10/2026');
		expect(shortDate('2026-10-13')).toBe('13/10');
		calendar.system = 'BS';
		expect(date('2026-10-13')).toBe('2083/06/27');
		expect(shortDate('2026-10-13')).toBe('06/27');
		expect(dateTime('2026-10-13T09:05:00')).toBe('2083/06/27 09:05');
		expect(firstOfMonth('2026-10-13')).toBe(bsToAd({ year: 2083, month: 6, day: 1 }));
	});

	it('fiscal year follows Shrawan 1', () => {
		expect(nepaliFiscalYear(new Date(2026, 6, 16))).toBe('2082-83');
		expect(nepaliFiscalYear(new Date(2026, 6, 17))).toBe('2083-84');
		expect(nepaliFiscalYear(new Date(2026, 9, 13))).toBe('2083-84');
	});
});
