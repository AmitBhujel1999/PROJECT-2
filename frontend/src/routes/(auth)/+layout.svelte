<script lang="ts">
	import { page } from '$app/state';

	let { children } = $props();
</script>

<div class="grid min-h-screen place-items-center bg-gradient-to-br from-slate-900 via-slate-800 to-blue-950 p-4">
	<div class="w-full max-w-sm">
		<div class="mb-6 text-center text-white">
			<img src="/favicon.svg" alt="" class="mx-auto mb-3 size-14 rounded-xl shadow-lg shadow-black/30" />
			<h1 class="text-xl font-semibold">Mero Lekhapal</h1>
			<p class="text-xs font-medium tracking-wide text-white/50 uppercase">Accounting &amp; Inventory</p>
			<p class="text-sm text-white/60">{page.url.pathname.startsWith('/companies') ? 'Create a company' : 'Sign in to continue'}</p>
		</div>
		<div class="rounded-xl border bg-background p-6 shadow-2xl">
			{@render children()}
		</div>
	</div>
</div>
