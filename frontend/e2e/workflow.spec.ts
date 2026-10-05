import { expect, test } from '@playwright/test';
import { expectToast, login, pick } from './helpers';

/**
 * The complete business workflow from the specification, executed against
 * the real application (SvelteKit -> nginx -> Django -> PostgreSQL).
 * Unique names make the test repeatable on a database that already has data.
 */
const run = Date.now().toString().slice(-6);
const SKU = `E2E-${run}`;
const PRODUCT = `E2E Widget ${run}`;
const CUSTOMER = `E2E Customer ${run}`;
const VENDOR = `E2E Vendor ${run}`;

test.describe.configure({ mode: 'serial' });

test('complete accounting & inventory workflow', async ({ page }) => {
	// ---- Login -> Home -> Dashboard -----------------------------------------
	await login(page);
	await expect(page.getByTestId('home-summary')).toBeVisible();
	await page.keyboard.press('Alt+c');
	await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Dashboard' }).click();
	await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
	await expect(page.getByTestId('dashboard-cards')).toContainText("Today's Sales");

	// ---- Create product -----------------------------------------------------
	await page.goto('/products');
	await page.getByTestId('new-product').click();
	await page.fill('#p-name', PRODUCT);
	await page.fill('#p-sku', SKU.toLowerCase());
	await page.fill('#p-cost', '1000');
	await page.fill('#p-price', '2000');
	await page.fill('#p-tax', '13');
	await page.fill('#p-reorder', '2');
	await page.fill('#p-opening', '0');
	await page.getByRole('button', { name: 'Create product' }).click();
	await expectToast(page, 'success', `Product ${PRODUCT} created`);
	await expect(page.getByTestId('products-table')).toContainText(SKU); // normalised to upper case

	// ---- Create customer & vendor ------------------------------------------
	for (const [path, name] of [['/customers', CUSTOMER], ['/vendors', VENDOR]] as const) {
		await page.goto(path);
		await page.getByTestId('new-party').click();
		await page.fill('#pf-name', name);
		await page.fill('#pf-phone', `98${run}00`);
		await page.selectOption('#pf-terms', 'DAYS_30');
		await page.getByRole('button', { name: /^Create (customer|vendor)$/ }).click();
		await expectToast(page, 'success', `${name} created successfully`);
	}

	// ---- Purchase with item + invoice discount ------------------------------
	// 10 x 1,000 = 10,000; item 10% = 1,000; invoice fixed 500
	// taxable 8,500; VAT 13% 1,105; total 9,605
	await page.goto('/purchases/new');
	await pick(page, '#party', VENDOR);
	await pick(page, 'input[id^=item-]', SKU, new RegExp(PRODUCT));
	await page.fill('[aria-label="Quantity row 1"]', '10');
	await page.fill('[aria-label="Price row 1"]', '1000');
	await page.selectOption('[aria-label="Discount type row 1"]', 'PERCENTAGE');
	await page.fill('[aria-label="Discount value row 1"]', '10');
	await page.selectOption('#inv-dtype', 'FIXED');
	await page.fill('#inv-dval', '500');
	await expect(page.getByTestId('t-total')).toContainText('9,605.00');
	await page.getByTestId('save-doc').click();
	await expectToast(page, 'success', /Purchase BILL-\d{6} recorded successfully/);
	await expect(page).toHaveURL(/\/purchases\/\d+$/);
	// Verify purchase discount (recalculated and stored by Django)
	await expect(page.getByTestId('d-subtotal')).toHaveText('10,000.00');
	await expect(page.getByTestId('d-item-discount')).toHaveText('1,000.00');
	await expect(page.getByTestId('d-invoice-discount')).toHaveText('500.00');
	await expect(page.getByTestId('d-taxable')).toHaveText('8,500.00');
	await expect(page.getByTestId('d-tax')).toHaveText('1,105.00');
	await expect(page.getByTestId('d-total')).toHaveText('9,605.00');
	const billNumber = (await page.getByRole('heading', { level: 1 }).textContent())!.replace('Bill ', '').trim();

	// ---- Verify stock increased ---------------------------------------------
	await page.goto('/products');
	await page.getByPlaceholder('Search name or SKU…').fill(SKU);
	await page.getByRole('link', { name: PRODUCT }).click();
	await expect(page.getByTestId('product-current-stock')).toContainText('10 Pcs');
	const productUrl = page.url();

	// ---- Sale with item + invoice discount ----------------------------------
	// 4 x 2,000 = 8,000; item fixed 400 -> 7,600; invoice 5% = 380
	// taxable 7,220; VAT 938.60; total 8,158.60
	await page.goto('/sales/new');
	await pick(page, '#party', CUSTOMER);
	await pick(page, 'input[id^=item-]', SKU, new RegExp(PRODUCT));
	await expect(page.locator('[aria-label="Price row 1"]')).toHaveValue('2000.00'); // auto-populated
	await page.fill('[aria-label="Quantity row 1"]', '4');
	await page.selectOption('[aria-label="Discount type row 1"]', 'FIXED');
	await page.fill('[aria-label="Discount value row 1"]', '400');
	await page.selectOption('#inv-dtype', 'PERCENTAGE');
	await page.fill('#inv-dval', '5');
	await expect(page.getByTestId('t-tax')).toHaveText('938.60');
	await expect(page.getByTestId('t-total')).toContainText('8,158.60');
	await page.getByTestId('save-doc').click();
	await expectToast(page, 'success', /Sale INV-\d{6} created successfully/);
	await expect(page.getByTestId('d-item-discount')).toHaveText('400.00');
	await expect(page.getByTestId('d-invoice-discount')).toHaveText('380.00');
	await expect(page.getByTestId('d-taxable')).toHaveText('7,220.00');
	await expect(page.getByTestId('d-tax')).toHaveText('938.60'); // Verify VAT
	await expect(page.getByTestId('d-total')).toHaveText('8,158.60');
	const saleUrl = page.url();
	const invoiceNumber = (await page.getByRole('heading', { level: 1 }).textContent())!.replace('Invoice ', '').trim();

	// ---- Verify stock decreased ---------------------------------------------
	await page.goto(productUrl);
	await expect(page.getByTestId('product-current-stock')).toContainText('6 Pcs');

	// ---- Customer receipt, allocated to the invoice -------------------------
	await page.goto('/receipts/new');
	await pick(page, '#pay-party', CUSTOMER);
	await page.fill('#pay-amount', '5000');
	await page.selectOption('#pay-method', 'BANK');
	await page.fill('#pay-ref', `TXN-${run}`);
	await page.getByTestId('alloc-manual').check();
	await page.getByLabel(`Allocate to ${invoiceNumber}`).fill('5000');
	await expect(page.getByTestId('alloc-remaining')).toHaveText('0.00');
	await page.getByTestId('save-payment').click();
	await expectToast(page, 'success', /Receipt RCPT-\d{6} recorded successfully/);
	await expect(page.getByTestId('p-allocated')).toHaveText('5,000.00');
	await expect(page.getByTestId('payment-allocations')).toContainText(invoiceNumber);

	// Invoice is now partially paid
	await page.goto(saleUrl);
	await expect(page.getByTestId('d-balance')).toHaveText('3,158.60');
	await expect(page.getByText('Partial').first()).toBeVisible();

	// ---- Verify customer balance, ledger and statement ----------------------
	await page.goto('/customers');
	await page.getByPlaceholder('Search customers…').fill(CUSTOMER);
	await page.getByRole('link', { name: CUSTOMER }).click();
	await expect(page.getByTestId('party-summary')).toContainText('8,158.60'); // total sales
	await expect(page.getByTestId('party-summary')).toContainText('5,000.00'); // received
	await expect(page.getByTestId('party-summary')).toContainText('3,158.60'); // outstanding
	const customerUrl = page.url().split('?')[0];
	await page.getByRole('tab', { name: 'Ledger' }).click();
	const ledger = page.getByTestId('ledger-table');
	await expect(ledger).toContainText(invoiceNumber);
	await expect(ledger).toContainText('8,158.60');
	await expect(ledger).toContainText('5,000.00');
	await expect(page.getByTestId('ledger-closing')).toHaveText('3,158.60');
	await page.getByRole('tab', { name: 'Statement' }).click();
	await expect(page.getByTestId('statement-closing')).toHaveText('3,158.60');

	// ---- Verify receivables aging -------------------------------------------
	await page.goto('/receivables/aging');
	await page.getByPlaceholder('Search customer…').fill(CUSTOMER);
	const agingRow = page.getByTestId('aging-table').getByRole('row', { name: new RegExp(CUSTOMER) });
	await expect(agingRow).toContainText('3,158.60'); // not yet due -> Current bucket
	await expect(agingRow.locator('td').nth(1)).toHaveText('3,158.60');

	// ---- Vendor payment (auto-allocated) and vendor ledger ------------------
	await page.goto('/payments/new');
	await pick(page, '#pay-party', VENDOR);
	await page.fill('#pay-amount', '9605');
	await page.selectOption('#pay-method', 'CHEQUE');
	await page.getByTestId('save-payment').click();
	await expectToast(page, 'success', /Payment PAY-\d{6} recorded successfully/);
	await expect(page.getByTestId('p-allocated')).toHaveText('9,605.00');
	await expect(page.getByTestId('payment-allocations')).toContainText(billNumber);

	await page.goto('/vendors');
	await page.getByPlaceholder('Search vendors…').fill(VENDOR);
	await page.getByRole('link', { name: VENDOR }).click();
	await page.getByRole('tab', { name: 'Ledger' }).click();
	await expect(page.getByTestId('ledger-table')).toContainText(billNumber);
	await expect(page.getByTestId('ledger-closing')).toHaveText('0.00');

	// ---- Verify payables aging (vendor fully paid -> not listed) ------------
	await page.goto('/payables/aging');
	await page.getByPlaceholder('Search vendor…').fill(VENDOR);
	await expect(page.getByTestId('aging-table')).toContainText('Nothing outstanding.');

	// ---- Attempt overselling -> blocked --------------------------------------
	await page.goto('/sales/new');
	await pick(page, '#party', CUSTOMER);
	await pick(page, 'input[id^=item-]', SKU, new RegExp(PRODUCT));
	await page.fill('[aria-label="Quantity row 1"]', '100');
	await page.getByTestId('save-doc').click();
	await expectToast(page, 'error', /Insufficient stock for .*Available: 6 Pcs, requested: 100 Pcs/);
	await expect(page).toHaveURL(/\/sales\/new$/); // sale blocked
	await page.goto(productUrl);
	await expect(page.getByTestId('product-current-stock')).toContainText('6 Pcs');

	// ---- Stock ledger ---------------------------------------------------------
	const ledgerRows = page.getByTestId('stock-ledger-table');
	await expect(ledgerRows).toContainText(billNumber);
	await expect(ledgerRows).toContainText(invoiceNumber);
	await page.goto('/inventory/ledger');
	await expect(page.getByRole('heading', { name: 'Stock Ledger' })).toBeVisible();
	await pick(page, '#ledger-product', SKU, new RegExp(PRODUCT));
	await expect(page.getByTestId('stock-ledger-table').locator('tbody tr')).toHaveCount(2);

	// ---- Sales report + CSV / PDF export ---------------------------------------
	await page.goto('/reports/sales');
	await pick(page, '#report-party', CUSTOMER);
	await expect(page.getByTestId('report-table').locator('tbody tr')).toHaveCount(1);
	await expect(page.getByTestId('report-table')).toContainText(invoiceNumber);
	await expect(page.getByTestId('report-summary')).toContainText('8,158.60');

	const csvDownload = page.waitForEvent('download');
	await page.getByTestId('export-csv').click();
	const csv = await csvDownload;
	expect(csv.suggestedFilename()).toMatch(/^sales-report-\d{8}\.csv$/);
	const csvText = (await import('node:fs')).readFileSync((await csv.path())!, 'utf8');
	expect(csvText).toContain(invoiceNumber);
	expect(csvText).toContain(CUSTOMER);
	expect(csvText.split('\n').filter((l) => l.includes('INV-')).length).toBe(1); // filter respected

	const pdfDownload = page.waitForEvent('download');
	await page.getByTestId('export-pdf').click();
	const pdf = await pdfDownload;
	const pdfBytes = (await import('node:fs')).readFileSync((await pdf.path())!);
	expect(pdfBytes.subarray(0, 4).toString()).toBe('%PDF');

	// ---- Purchase report ---------------------------------------------------------
	await page.goto('/reports/purchases');
	await pick(page, '#report-party', VENDOR);
	await expect(page.getByTestId('report-table')).toContainText(billNumber);
	await expect(page.getByTestId('report-summary')).toContainText('9,605.00');

	// ---- Invoice PDF + print -------------------------------------------------------
	await page.goto(saleUrl);
	const invoicePdf = page.waitForEvent('download');
	await page.getByTestId('download-pdf').click();
	const invoiceFile = await invoicePdf;
	expect(invoiceFile.suggestedFilename()).toBe(`${invoiceNumber}.pdf`);
	const printResponse = page.waitForResponse((r) => r.url().includes('/pdf/?inline=1'));
	await page.getByTestId('print-doc').click();
	const printed = await printResponse;
	expect(printed.status()).toBe(200);
	expect(printed.headers()['content-type']).toBe('application/pdf');

	// Back to the customer to make sure nothing changed after exports
	await page.goto(customerUrl + '?tab=ledger');
	await expect(page.getByTestId('ledger-closing')).toHaveText('3,158.60');
});
