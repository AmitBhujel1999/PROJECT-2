import { expect, test } from '@playwright/test';
import { login } from './helpers';

test('unauthenticated users are redirected to login', async ({ page }) => {
	await page.goto('/sales');
	await expect(page).toHaveURL(/\/login\?next=%2Fsales/);
});

test('API rejects unauthenticated requests and missing CSRF token', async ({ request }) => {
	const r = await request.get('/api/products/');
	expect(r.status()).toBe(401);
	expect((await r.json()).error.code).toBe('AUTHENTICATION_REQUIRED');
	const login = await request.post('/api/auth/login/', { data: { username: 'admin', password: 'x' } });
	expect(login.status()).toBe(403); // no CSRF token
});

test('session cookie is HTTP-only and nothing sensitive is in localStorage', async ({ page, context }) => {
	await login(page);
	const cookies = await context.cookies();
	const session = cookies.find((c) => c.name === 'acct_sessionid');
	expect(session?.httpOnly).toBe(true);
	const storage = await page.evaluate(() => JSON.stringify(localStorage));
	expect(storage).not.toMatch(/session|csrf|token|password/i);
});

test('staff role cannot reach finance features (server-enforced)', async ({ page }) => {
	await login(page, 'demo_staff', 'Demo@12345');
	await expect(page.getByRole('link', { name: 'Sales Report' })).toHaveCount(0);
	const res = await page.evaluate(async () => (await fetch('/api/reports/sales/')).status);
	expect(res).toBe(403);
});

test('mobile drawer navigation works', async ({ browser }) => {
	const context = await browser.newContext({ viewport: { width: 390, height: 844 }, baseURL: test.info().project.use.baseURL });
	const page = await context.newPage();
	await login(page);
	await page.getByTestId('mobile-menu').click();
	await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Customers' }).click();
	await expect(page).toHaveURL(/\/customers$/);
	await expect(page.getByRole('heading', { name: 'Customers' })).toBeVisible();
	await context.close();
});
