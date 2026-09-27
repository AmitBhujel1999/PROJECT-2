import { redirect } from '@sveltejs/kit';

// The stock report is the stock register (same filters and exports).
export function load() {
	redirect(307, '/inventory');
}
