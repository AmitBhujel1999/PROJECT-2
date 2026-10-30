import { expect, test } from '@playwright/test';
import { expectToast, login, pick } from './helpers';

/**
 * A product's own sale / purchase discount is filled into new entry lines and
 * can still be changed there. Fixed discounts are per unit, so they follow the quantity.
 */
const run = Date.now().toString().slice(-6);
const SKU = `DSC-${run}`;
const PRODUCT = `Discount Widget ${run}`;
const CUSTOMER = `Disc Customer ${run}`;
const VENDOR = `Disc Vendor ${run}`;

test('product discount fills sale and purchase lines', async ({ page }) => {
	await login(page);
	// A fresh company asks once which date system to use.
	const dates = page.getByRole('dialog', { name: 'Choose date system' });
	if (await dates.isVisible().catch(() => false)) await dates.getByRole('button', { name: 'Continue' }).click();

	await page.goto('/products');
	await page.getByTestId('new-product').click();
	await page.fill('#p-name', PRODUCT);
	await page.fill('#p-sku', SKU);
	await page.fill('#p-cost', '1000');
	await page.fill('#p-price', '2000');
	await page.fill('#p-opening', '20');
	await page.selectOption('#p-sdisc', 'PERCENTAGE');
	await page.fill('[aria-label="Sale discount value"]', '10');
	await page.selectOption('#p-pdisc', 'FIXED');
	await page.fill('[aria-label="Purchase discount value"]', '50');
	await page.screenshot({ path: 'test-results/product-discount-form.png' });
	await page.getByRole('button', { name: 'Create product' }).click();
	await expectToast(page, 'success', `Product ${PRODUCT} created`);
	await expect(page.getByTestId('products-table')).toContainText('10%');

	for (const [path, name] of [['/customers', CUSTOMER], ['/vendors', VENDOR]] as const) {
		await page.goto(path);
		await page.getByTestId('new-party').click();
		await page.fill('#pf-name', name);
		await page.getByRole('button', { name: /^Create (customer|vendor)$/ }).click();
		await expectToast(page, 'success', `${name} created successfully`);
	}

	// Sale: 3 x 2,000 = 6,000; product discount 10% = 600; taxable 5,400; VAT 702; total 6,102
	await page.goto('/sales/new');
	await pick(page, '#party', CUSTOMER);
	await pick(page, 'input[id^=item-]', SKU, new RegExp(PRODUCT));
	await expect(page.locator('[aria-label="Discount type row 1"]')).toHaveValue('PERCENTAGE');
	await expect(page.locator('[aria-label="Discount value row 1"]')).toHaveValue('10');
	await page.fill('[aria-label="Quantity row 1"]', '3');
	await expect(page.getByTestId('t-item-discount')).toHaveText('600.00');
	await expect(page.getByTestId('t-total')).toContainText('6,102.00');
	await page.screenshot({ path: 'test-results/product-discount-sale.png' });
	await page.getByTestId('save-doc').click();
	await expectToast(page, 'success', /Sale INV-\d{6} created successfully/);
	await expect(page.getByTestId('d-item-discount')).toHaveText('600.00');

	// Purchase: Rs. 50 off each unit follows the quantity until the user edits the discount.
	await page.goto('/purchases/new');
	await pick(page, '#party', VENDOR);
	await pick(page, 'input[id^=item-]', SKU, new RegExp(PRODUCT));
	await expect(page.locator('[aria-label="Discount type row 1"]')).toHaveValue('FIXED');
	await expect(page.locator('[aria-label="Discount value row 1"]')).toHaveValue('50.00');
	await page.fill('[aria-label="Quantity row 1"]', '4');
	await expect(page.locator('[aria-label="Discount value row 1"]')).toHaveValue('200.00');
	await expect(page.getByTestId('t-item-discount')).toHaveText('200.00');
	await page.fill('[aria-label="Discount value row 1"]', '150');
	await page.fill('[aria-label="Quantity row 1"]', '5');
	await expect(page.locator('[aria-label="Discount value row 1"]')).toHaveValue('150');
	await expect(page.getByTestId('t-item-discount')).toHaveText('150.00');
	await page.screenshot({ path: 'test-results/product-discount-purchase.png' });
});
