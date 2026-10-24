<script lang="ts">
	import { ListState } from '$lib/stores/list.svelte';
	import { money, qty, date as fmtDate } from '$lib/utilities/format';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';

	interface Row {
		id: number;
		sku_code: string;
		name: string;
		unit: string;
		quantity: string;
		sales: string;
		average_cost: string;
		cost: string;
		profit: string;
		margin: string | null;
	}
	interface Summary {
		products: number;
		sales: string;
		cost: string;
		profit: string;
		margin: string | null;
		loss_making: number;
		start_date: string;
		end_date: string;
	}

	const path = 'reports/product-profit/';
	const list = new ListState<Row, Summary>(path, { ordering: '-profit' });
	list.load();

	let start = $state('');
	let end = $state('');
	let ordering = $state('-profit');
	const s = $derived(list.summary);
	const SORTS = [
		{ value: '-profit', label: 'Most profit first' },
		{ value: 'profit', label: 'Least profit first' },
		{ value: '-margin', label: 'Highest margin first' },
		{ value: 'margin', label: 'Lowest margin first' },
		{ value: '-sales', label: 'Most sales first' },
		{ value: '-quantity', label: 'Most quantity first' },
		{ value: 'name', label: 'Item name A–Z' }
	];
	const negative = (v: string | null) => v !== null && Number(v) < 0;
	const bar = (v: string | null) => Math.max(0, Math.min(100, Number(v ?? 0)));
</script>

<svelte:head><title>Product Profitability · Mero Lekhapal</title></svelte:head>

<PageHeader title="Product Profitability" description="Which items earn the most. Sales are after discounts, without VAT; cost is the weighted average purchase cost, as in the Profit & Loss.">
	{#snippet actions()}<ExportButtons {path} query={list.filters} />{/snippet}
</PageHeader>

<Card class="no-print mb-4">
	<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
		<label class="grid gap-1 text-xs text-muted-foreground">Start date<Input type="date" bind:value={start} onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">End date<Input type="date" bind:value={end} onchange={() => list.set('end_date', end)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">Item<SearchInput placeholder="Search item or SKU…" onSearch={(v) => list.set('search', v)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">Sort<Select bind:value={ordering} onchange={() => list.set('ordering', ordering)} options={SORTS} /></label>
	</div>
	{#if s}<p class="mt-2 text-xs text-muted-foreground">Period: {fmtDate(s.start_date)} – {fmtDate(s.end_date)} (default: this fiscal year to date)</p>{/if}
</Card>

{#if s}
	<div class="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4" data-testid="report-summary">
		{#each [['Sales (excl. VAT)', money(s.sales), false], ['Cost of sales', money(s.cost), false], ['Gross profit', money(s.profit), negative(s.profit)], ['Margin', s.margin === null ? '—' : `${s.margin}%`, negative(s.margin)]] as [label, value, bad] (label)}
			<div class="rounded-xl border bg-card p-3 shadow-xs">
				<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
				<p class="mt-1 font-semibold tabular-nums" class:text-red-600={bad}>{value}</p>
			</div>
		{/each}
	</div>
	{#if s.loss_making}
		<p class="-mt-2 mb-4 rounded-md bg-red-50 px-3 py-2 text-sm text-red-800">
			{s.loss_making} item{s.loss_making === 1 ? ' was' : 's were'} sold below cost in this period. Sort by "Least profit first" to see {s.loss_making === 1 ? 'it' : 'them'}.
		</p>
	{/if}
{/if}

<Card bodyClass="p-0">
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="report-table">
			<thead>
				<tr><th>Item</th><th>SKU</th><th class="num">Qty sold</th><th class="num">Sales</th><th class="num">Avg cost</th><th class="num">Cost of sales</th><th class="num">Gross profit</th><th class="w-40">Margin</th></tr>
			</thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={8} />
				{#each list.items as r (r.id)}
					<tr>
						<td><a class="font-medium text-primary hover:underline" href="/products/{r.id}">{r.name}</a></td>
						<td class="text-muted-foreground">{r.sku_code}</td>
						<td class="num">{qty(r.quantity)} {r.unit}</td>
						<td class="num">{money(r.sales)}</td>
						<td class="num text-muted-foreground">{money(r.average_cost)}</td>
						<td class="num">{money(r.cost)}</td>
						<td class="num font-medium" class:text-red-600={negative(r.profit)}>{money(r.profit)}</td>
						<td>
							<div class="flex items-center gap-2">
								<div class="h-2 flex-1 rounded-full bg-muted"><div class="h-2 rounded-full {negative(r.margin) ? 'bg-red-500' : 'bg-green-600'}" style="width: {negative(r.margin) ? 100 : bar(r.margin)}%"></div></div>
								<span class="w-14 text-right text-xs tabular-nums" class:text-red-600={negative(r.margin)}>{r.margin === null ? '—' : `${r.margin}%`}</span>
							</div>
						</td>
					</tr>
				{/each}
			</tbody>
			{#if s && list.items.length}
				<tfoot>
					<tr>
						<td colspan="3">TOTAL ({s.products} item{s.products === 1 ? '' : 's'})</td>
						<td class="num">{money(s.sales)}</td>
						<td></td>
						<td class="num">{money(s.cost)}</td>
						<td class="num" class:text-red-600={negative(s.profit)}>{money(s.profit)}</td>
						<td class="text-right text-xs tabular-nums">{s.margin === null ? '' : `${s.margin}%`}</td>
					</tr>
				</tfoot>
			{/if}
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
