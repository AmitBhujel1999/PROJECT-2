import { expect, test } from '@playwright/test';
import { expectToast, login } from './helpers';

/**
 * Backups: choose a folder (typed, as on a computer without the folder chooser),
 * back up, and see the backup listed ready to restore.
 * E2E_BACKUP_FOLDER must be an existing folder on the computer running the server.
 */
const FOLDER = process.env.E2E_BACKUP_FOLDER;

test('choose a backup folder and back up', async ({ page }) => {
	test.skip(!FOLDER, 'E2E_BACKUP_FOLDER not set');
	await login(page);
	const dates = page.getByRole('dialog', { name: 'Choose date system' });
	if (await dates.isVisible().catch(() => false)) await dates.getByRole('button', { name: 'Continue' }).click();

	await page.goto('/settings/backup');
	await expect(page.getByText('Off. Choose a folder to start.')).toBeVisible();
	await page.fill('[aria-label="Backup folder"]', FOLDER!);
	await page.getByRole('button', { name: 'Save folder' }).click();
	await expectToast(page, 'success', 'Backup folder saved');
	await page.getByRole('button', { name: 'Back up now' }).click();
	await expectToast(page, 'success', 'Backup done');
	await expect(page.getByRole('button', { name: 'Restore' })).toHaveCount(1);
	await page.screenshot({ path: 'test-results/backup-page.png' });
});
