<script lang="ts">
	import { page } from '$app/state';
	import { ListState } from '$lib/stores/list.svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { money, qty } from '$lib/utilities/format';
	import type { StockRow } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';

	let status = $state(page.url.searchParams.get('status') ?? '');
	let asOf = $state('');
	let start = $state('');
	let ordering = $state('name');
	const list = new ListState<StockRow, Record<string, any>>('reports/stock/', { status: page.url.searchParams.get('status') ?? '', is_active: 'true', ordering: 'name' });
	list.load();
	const s = $derived(list.summary);
</script>

<svelte:head><title>Stock Register · Accounting</title></svelte:head>

<PageHeader title="Stock Register" description="Stock is derived from the immutable stock ledger. Pick an 'as of' date for historical stock.">
	{#snippet actions()}<ExportButtons path="reports/stock/" query={list.filters} />{/snippet}
</PageHeader>

{#if s}
	<div class="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-5">
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Products</p><p class="text-lg font-semibold">{s.total_products}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Cost value</p><p class="text-lg font-semibold tabular-nums">{auth.currency} {money(s.total_cost_value)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Retail value</p><p class="text-lg font-semibold tabular-nums">{auth.currency} {money(s.total_retail_value)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Low stock</p><p class="text-lg font-semibold text-amber-700">{s.low_stock_count}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Out of stock</p><p class="text-lg font-semibold text-red-600">{s.out_of_stock_count}</p></div>
	</div>
{/if}

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<SearchInput placeholder="Search item or SKU…" onSearch={(v) => list.set('search', v)} />
		<Select class="w-40" bind:value={status} onchange={() => list.set('status', status)} aria-label="Stock status"
			options={[{ value: 'IN_STOCK', label: 'In stock' }, { value: 'LOW_STOCK', label: 'Low stock' }, { value: 'OUT_OF_STOCK', label: 'Out of stock' }]} placeholder="All statuses" />
		<label class="grid gap-1 text-xs text-muted-foreground">Movements from<Input type="date" bind:value={start} class="h-9 w-40" onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">Stock as of<Input type="date" bind:value={asOf} class="h-9 w-40" onchange={() => list.set('as_of', asOf)} data-testid="stock-as-of" /></label>
		<Select class="w-44" bind:value={ordering} onchange={() => list.set('ordering', ordering)} aria-label="Sort"
			options={[{ value: 'name', label: 'Sort: Name' }, { value: 'current_stock', label: 'Sort: Stock ↑' }, { value: '-cost_value', label: 'Sort: Value ↓' }, { value: '-total_sold', label: 'Sort: Most sold' }]} />
	</div>
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="stock-register">
			<thead>
				<tr><th>SKU</th><th>Item</th><th>Unit</th><th class="num">Purchased</th><th class="num">Sold</th><th class="num">Adjusted</th><th class="num">Current stock</th><th class="num">Cost value</th><th class="num">Retail value</th><th class="num">Reorder</th><th>Status</th></tr>
			</thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={11} />
				{#each list.items as r (r.id)}
					<tr>
						<td class="font-mono text-xs">{r.sku_code}</td>
						<td><a class="font-medium text-primary hover:underline" href="/products/{r.id}">{r.name}</a></td>
						<td>{r.unit}</td>
						<td class="num">{qty(r.total_purchased)}</td>
						<td class="num">{qty(r.total_sold)}</td>
						<td class="num">{qty(r.total_adjusted)}</td>
						<td class="num font-semibold">{qty(r.current_stock)}</td>
						<td class="num">{money(r.cost_value)}</td>
						<td class="num">{money(r.retail_value)}</td>
						<td class="num">{qty(r.reorder_level)}</td>
						<td><StatusBadge status={r.stock_status} /></td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
