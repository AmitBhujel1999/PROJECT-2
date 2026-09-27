<script lang="ts">
	import { ChevronLeft, ChevronRight } from '@lucide/svelte';
	import Button from './button.svelte';

	let {
		page,
		totalPages,
		count,
		pageSize,
		onPage,
		onPageSize
	}: { page: number; totalPages: number; count: number; pageSize: number; onPage: (p: number) => void; onPageSize: (s: number) => void } = $props();

	const from = $derived(count === 0 ? 0 : (page - 1) * pageSize + 1);
	const to = $derived(Math.min(page * pageSize, count));
</script>

<div class="no-print flex flex-wrap items-center justify-between gap-3 border-t px-3 py-2.5 text-sm">
	<p class="text-muted-foreground" aria-live="polite">Showing <b>{from}</b>–<b>{to}</b> of <b>{count}</b></p>
	<div class="flex items-center gap-2">
		<label class="flex items-center gap-1.5 text-muted-foreground">
			Rows
			<select class="h-8 rounded-md border bg-background px-1.5 text-sm" value={pageSize} onchange={(e) => onPageSize(Number(e.currentTarget.value))} aria-label="Rows per page">
				{#each [25, 50, 100] as s (s)}<option value={s}>{s}</option>{/each}
			</select>
		</label>
		<Button variant="outline" size="sm" disabled={page <= 1} onclick={() => onPage(page - 1)} aria-label="Previous page"><ChevronLeft /></Button>
		<span class="tabular-nums">{page} / {totalPages}</span>
		<Button variant="outline" size="sm" disabled={page >= totalPages} onclick={() => onPage(page + 1)} aria-label="Next page"><ChevronRight /></Button>
	</div>
</div>
