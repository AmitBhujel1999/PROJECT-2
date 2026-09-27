import { api, ApiError, type Query } from '$lib/api/client';
import type { Paginated } from '$lib/types';

/**
 * Server-side paginated list state. Filtering, searching, sorting and
 * pagination all happen in Django; the browser only holds the current page.
 */
export class ListState<T, S = Record<string, any>> {
	items = $state<T[]>([]);
	count = $state(0);
	page = $state(1);
	pageSize = $state(25);
	totalPages = $state(1);
	loading = $state(false);
	error = $state<string | null>(null);
	summary = $state<S | null>(null);
	extra = $state<Record<string, any>>({});
	params = $state<Query>({});
	#controller: AbortController | null = null;

	constructor(
		public path: string,
		initial: Query = {}
	) {
		this.params = { ...initial };
	}

	get query(): Query {
		return { ...this.params, page: this.page, page_size: this.pageSize };
	}

	/** Filters only (for exports). */
	get filters(): Query {
		return { ...this.params };
	}

	async load(): Promise<void> {
		this.#controller?.abort();
		const controller = new AbortController();
		this.#controller = controller;
		this.loading = true;
		this.error = null;
		try {
			const data = await api.get<Paginated<T, S>>(this.path, this.query, controller.signal);
			const { results, count, page, page_size, total_pages, summary, ...rest } = data;
			this.items = results;
			this.count = count;
			this.page = page;
			this.pageSize = page_size;
			this.totalPages = total_pages;
			this.summary = (summary ?? null) as S | null;
			this.extra = rest;
		} catch (err) {
			if ((err as Error).name === 'AbortError') return;
			this.error = err instanceof ApiError ? err.message : 'Failed to load data.';
			if (err instanceof ApiError && err.status === 404 && this.page > 1) {
				this.page = 1;
				return this.load();
			}
		} finally {
			if (this.#controller === controller) this.loading = false;
		}
	}

	set(key: string, value: Query[string]) {
		this.params[key] = value;
		this.page = 1;
		return this.load();
	}

	setMany(values: Query) {
		Object.assign(this.params, values);
		this.page = 1;
		return this.load();
	}

	setPage = (p: number) => {
		this.page = p;
		this.load();
	};

	setPageSize = (s: number) => {
		this.pageSize = s;
		this.page = 1;
		this.load();
	};
}
