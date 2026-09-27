<script lang="ts">
	import { toast } from '$lib/stores/toast.svelte';
	import { CircleCheck, CircleX, TriangleAlert, Info, X } from '@lucide/svelte';

	const styles = {
		success: 'border-emerald-200 bg-emerald-50 text-emerald-900',
		error: 'border-red-200 bg-red-50 text-red-900',
		warning: 'border-amber-200 bg-amber-50 text-amber-900',
		info: 'border-sky-200 bg-sky-50 text-sky-900'
	};
</script>

<div class="no-print pointer-events-none fixed bottom-4 right-4 z-[100] flex w-[calc(100vw-2rem)] max-w-sm flex-col gap-2" aria-live="polite" aria-atomic="false">
	{#each toast.items as t (t.id)}
		<div class="pointer-events-auto flex items-start gap-2 rounded-lg border px-3 py-2.5 text-sm shadow-lg {styles[t.kind]}" role={t.kind === 'error' ? 'alert' : 'status'} data-testid="toast-{t.kind}">
			{#if t.kind === 'success'}<CircleCheck class="mt-0.5 size-4 shrink-0" />
			{:else if t.kind === 'error'}<CircleX class="mt-0.5 size-4 shrink-0" />
			{:else if t.kind === 'warning'}<TriangleAlert class="mt-0.5 size-4 shrink-0" />
			{:else}<Info class="mt-0.5 size-4 shrink-0" />{/if}
			<p class="flex-1">{t.message}</p>
			<button class="rounded p-0.5 opacity-60 hover:opacity-100" onclick={() => toast.dismiss(t.id)} aria-label="Dismiss notification">
				<X class="size-3.5" />
			</button>
		</div>
	{/each}
</div>
