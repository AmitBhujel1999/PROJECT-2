import { api, ApiError } from '$lib/api/client';
import type { BusinessSettings, User } from '$lib/types';

/**
 * Session state. The authoritative session lives in Django (HTTP-only cookie);
 * this only mirrors who is logged in and which permissions to *display*.
 * Every permission is re-checked by the API.
 */
class AuthState {
	user = $state<User | null>(null);
	settings = $state<BusinessSettings | null>(null);
	loaded = $state(false);

	get permissions(): Set<string> {
		return new Set(this.user?.permissions ?? []);
	}

	can(perm: string): boolean {
		return this.permissions.has(perm);
	}

	async load(): Promise<User | null> {
		try {
			this.user = await api.get<User>('auth/me/');
			this.loadSettings();
		} catch (err) {
			if (!(err instanceof ApiError) || err.status !== 401) console.error(err);
			this.user = null;
		} finally {
			this.loaded = true;
		}
		return this.user;
	}

	async loadSettings() {
		try {
			this.settings = await api.get<BusinessSettings>('settings/');
		} catch {
			/* settings are optional for rendering */
		}
	}

	async login(username: string, password: string): Promise<User> {
		const res = await api.post<User>('auth/login/', { username, password });
		this.user = res.data;
		this.loadSettings();
		return res.data;
	}

	async logout() {
		try {
			await api.post('auth/logout/');
		} finally {
			this.user = null;
		}
	}

	get currency(): string {
		return this.settings?.currency_code ?? 'NPR';
	}

	get taxLabel(): string {
		return this.settings?.tax_label ?? 'VAT';
	}
}

export const auth = new AuthState();
