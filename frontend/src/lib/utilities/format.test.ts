import { describe, expect, it } from 'vitest';
import { addDays, date, money, qty } from './format';

describe('money', () => {
	it('formats decimal strings with lakh grouping', () => {
		expect(money('1234567.5')).toBe('12,34,567.50');
		expect(money('10170.00')).toBe('10,170.00');
		expect(money('999')).toBe('999.00');
		expect(money('-261.00')).toBe('-261.00');
		expect(money('0.00', true)).toBe('-');
		expect(money(null)).toBe('');
	});
	it('does not introduce float rounding errors', () => {
		expect(money('0.10')).toBe('0.10');
		expect(money('123456789012.99')).toBe('1,23,45,67,89,012.99');
	});
});

describe('dates', () => {
	it('formats ISO as dd/mm/yyyy', () => {
		expect(date('2026-09-27')).toBe('27/09/2026');
	});
	it('adds days across month boundaries', () => {
		expect(addDays('2026-09-01', 30)).toBe('2026-10-01');
		expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
	});
});

describe('qty', () => {
	it('trims trailing zeros', () => {
		expect(qty('5.000')).toBe('5');
		expect(qty('2.500')).toBe('2.5');
	});
});

import { fromCents, toCents } from './format';
describe('cents', () => {
	it('round-trips decimal strings exactly', () => {
		expect(toCents('10170.5')).toBe(1017050n);
		expect(fromCents(toCents('0.1') + toCents('0.2'))).toBe('0.30');
		expect(fromCents(toCents('-5.05'))).toBe('-5.05');
	});
});
