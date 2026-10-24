import { describe, expect, it } from 'vitest';
import { calculate, fmt, type DiscountType, type LineIn } from './calc';

// The same cases as desktop/tests/Accounting.Tests/CalculationTests.cs: the phone must agree with the PC.
const line = (quantity: number | string, unitPrice: number | string, taxRate: number, discountType: DiscountType = null, discountValue: number | string = 0): LineIn => ({
	quantity,
	unitPrice,
	taxRate,
	discountType,
	discountValue
});
const m = (v: bigint) => fmt(v, 2);

describe('phone totals match the PC', () => {
	it('purchase example: 10 x 1,000, 10% discount, 13% VAT', () => {
		const t = calculate([line(10, 1000, 13, 'PERCENTAGE', 10)], null, 0);
		const l = t.lines[0];
		expect([m(l.gross), m(l.discount), m(l.taxable), m(l.tax), m(l.total), m(t.total)]).toEqual(['10000.00', '1000.00', '9000.00', '1170.00', '10170.00', '10170.00']);
	});

	it('fixed item discount', () => {
		const t = calculate([line(2, 750, 13, 'FIXED', 100)], null, 0);
		expect([m(t.lines[0].discount), m(t.taxable), m(t.tax), m(t.total)]).toEqual(['100.00', '1400.00', '182.00', '1582.00']);
	});

	it('invoice percentage discount after item discount', () => {
		const t = calculate([line(5, 500, 13, 'PERCENTAGE', 10), line(2, 750, 13)], 'PERCENTAGE', 10);
		expect([m(t.subtotal), m(t.itemDiscounts), m(t.netSubtotal), m(t.discount), m(t.totalDiscount), m(t.taxable), m(t.tax), m(t.total)]).toEqual([
			'4000.00', '250.00', '3750.00', '375.00', '625.00', '3375.00', '438.75', '3813.75'
		]);
	});

	it('invoice fixed discount allocated pro-rata and sums exactly', () => {
		const t = calculate([line(1, 100, 13), line(1, 100, 13), line(1, 100, 13)], 'FIXED', 100);
		expect(m(t.lines.reduce((a, l) => a + l.share, 0n))).toBe('100.00');
		expect(m(t.taxable)).toBe('200.00');
	});

	it('mixed tax rates computed per line', () => {
		const t = calculate([line(1, 1000, 13), line(1, 1000, 0)], 'FIXED', 200);
		expect([m(t.lines[0].taxable), m(t.lines[0].tax), m(t.lines[1].tax), m(t.total)]).toEqual(['900.00', '117.00', '0.00', '1917.00']);
	});

	it('rounds half up', () => {
		const t = calculate([line(3, '33.33', 13)], null, 0);
		expect([m(t.tax), m(t.total)]).toEqual(['13.00', '112.99']);
	});

	it('fractional quantities', () => {
		expect(m(calculate([line('2.5', 140, 0)], null, 0).subtotal)).toBe('350.00');
	});

	it.each([
		[line(1, 100, 13, 'FIXED', 150), null, 0, 'INVALID_DISCOUNT'],
		[line(1, 100, 13, 'PERCENTAGE', 101), null, 0, 'INVALID_DISCOUNT'],
		[line(1, 100, 13, 'PERCENTAGE', -1), null, 0, 'INVALID_DISCOUNT'],
		[line(1, 100, 13), 'FIXED', 101, 'INVALID_DISCOUNT'],
		[line(0, 100, 13), null, 0, 'INVALID_QUANTITY'],
		[line(1, -1, 13), null, 0, 'INVALID_PRICE'],
		[line(1, 1, 101), null, 0, 'INVALID_TAX_RATE']
	] as [LineIn, DiscountType, number, string][])('rejects bad input (%#)', (l, type, value, code) => {
		expect(() => calculate([l], type, value)).toThrow(expect.objectContaining({ code }));
	});

	it('full discount gives zero, not negative', () => {
		const t = calculate([line(1, 100, 13, 'PERCENTAGE', 100)], null, 0);
		expect([m(t.taxable), m(t.total)]).toEqual(['0.00', '0.00']);
	});

	it('no items rejected', () => expect(() => calculate([], null, 0)).toThrow());
});
