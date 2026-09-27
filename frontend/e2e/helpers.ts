import { expect, type Page } from '@playwright/test';

export const ADMIN = { user: process.env.E2E_USER ?? 'admin', password: process.env.E2E_PASSWORD ?? 'Admin@12345' };

export async function login(page: Page, user = ADMIN.user, password = ADMIN.password) {
	await page.goto('/login');
	await page.fill('#username', user);
	await page.fill('#password', password);
	await page.click('button[type=submit]');
	await page.waitForURL(/\/dashboard/);
}

/** Pick an option in one of our async comboboxes (server-side search). */
export async function pick(page: Page, inputSelector: string, search: string, optionText: string | RegExp = search) {
	const input = page.locator(inputSelector);
	await input.click();
	await input.fill(search);
	const option = page.getByRole('option', { name: optionText }).first();
	await expect(option).toBeVisible();
	await option.click();
}

export async function expectToast(page: Page, kind: 'success' | 'error' | 'warning', text: string | RegExp) {
	await expect(page.getByTestId(`toast-${kind}`).filter({ hasText: text }).first()).toBeVisible();
}
