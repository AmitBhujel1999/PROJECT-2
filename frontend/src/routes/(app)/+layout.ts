import { redirect } from '@sveltejs/kit';
import { auth } from '$lib/stores/auth.svelte';

export async function load({ url }) {
	if (!auth.user) await auth.load();
	if (!auth.user) {
		redirect(307, `/login?next=${encodeURIComponent(url.pathname + url.search)}`);
	}
	return {};
}
