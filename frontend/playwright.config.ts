import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end tests run against a running stack (default: docker compose on
 * http://localhost). Override with E2E_BASE_URL, E2E_USER, E2E_PASSWORD.
 */
export default defineConfig({
	testDir: './e2e',
	timeout: 120_000,
	expect: { timeout: 15_000 },
	fullyParallel: false,
	workers: 1,
	retries: 0,
	reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
	outputDir: 'test-results',
	use: {
		baseURL: process.env.E2E_BASE_URL ?? 'http://localhost',
		trace: 'retain-on-failure',
		screenshot: 'only-on-failure',
		acceptDownloads: true
	},
	projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }]
});
