import { api } from '$lib/api/client';
import type { MoneyAccount, Paginated } from '$lib/types';

/**
 * Cash/bank accounts, loaded once and shared by every account picker.
 * Call `refresh()` after adding or editing an account.
 */
class AccountsState {
	all = $state<MoneyAccount[]>([]);
	loaded = $state(false);
	#pending: Promise<void> | null = null;

	get active(): MoneyAccount[] {
		return this.all.filter((a) => a.is_active);
	}

	/** Account used when the user has not picked one: the first cash account. */
	get fallback(): MoneyAccount | undefined {
		return this.active.find((a) => a.kind === 'CASH') ?? this.active[0];
	}

	ensure(): Promise<void> {
		if (this.loaded) return Promise.resolve();
		return (this.#pending ??= this.refresh());
	}

	async refresh(): Promise<void> {
		try {
			const page = await api.get<Paginated<MoneyAccount>>('cash-bank/accounts/', { page_size: 200, ordering: 'name' });
			this.all = page.results;
			this.loaded = true;
		} finally {
			this.#pending = null;
		}
	}
}

export const accounts = new AccountsState();
