import { redirect } from '@sveltejs/kit';
import { auth } from '$lib/stores/auth.svelte';
import { cloudMode } from '$lib/cloud/cloud';

export async function load({ url }) {
	if (!auth.user) await auth.load();
	if (!auth.user) {
		if (cloudMode) redirect(307, '/link');
		redirect(307, `/login?next=${encodeURIComponent(url.pathname + url.search)}`);
	}
	return {};
}
