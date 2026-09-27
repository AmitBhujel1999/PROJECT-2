<script lang="ts">
	let { children } = $props();
</script>

<div class="grid min-h-screen place-items-center bg-gradient-to-br from-slate-900 via-slate-800 to-blue-950 p-4">
	<div class="w-full max-w-sm">
		<div class="mb-6 text-center text-white">
			<div class="mx-auto mb-3 grid size-12 place-items-center rounded-xl bg-primary text-xl font-bold">A</div>
			<h1 class="text-xl font-semibold">Accounting & Inventory</h1>
			<p class="text-sm text-white/60">Sign in to continue</p>
		</div>
		<div class="rounded-xl border bg-background p-6 shadow-2xl">
			{@render children()}
		</div>
	</div>
</div>
