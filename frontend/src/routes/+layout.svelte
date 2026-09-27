<script lang="ts">
	import '../app.css';
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { onUnauthorized } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import Toaster from '$lib/components/ui/toaster.svelte';

	let { children } = $props();

	const queryClient = new QueryClient({
		defaultOptions: { queries: { staleTime: 30_000, refetchOnWindowFocus: false, retry: 1 } }
	});

	onUnauthorized(() => {
		auth.user = null;
		const path = page.url.pathname;
		if (!['/login', '/forgot-password', '/reset-password'].includes(path)) {
			goto(`/login?next=${encodeURIComponent(path + page.url.search)}`);
		}
	});
</script>

<QueryClientProvider client={queryClient}>
	{@render children()}
</QueryClientProvider>
<Toaster />
