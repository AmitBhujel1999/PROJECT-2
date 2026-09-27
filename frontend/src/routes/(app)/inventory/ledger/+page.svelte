<script lang="ts">
	import { page } from '$app/state';
	import { api } from '$lib/api/client';
	import { ListState } from '$lib/stores/list.svelte';
	import type { Paginated, Product, StockLedgerRow } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Combobox from '$lib/components/ui/combobox.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import StockLedgerTable from '$lib/components/inventory/stock-ledger-table.svelte';

	const TYPES = [
		{ value: 'OPENING_STOCK', label: 'Opening stock' },
		{ value: 'PURCHASE', label: 'Purchase' },
		{ value: 'SALE', label: 'Sale' },
		{ value: 'ADJUSTMENT', label: 'Adjustment' },
		{ value: 'PURCHASE_CANCEL', label: 'Purchase cancellation' },
		{ value: 'SALE_CANCEL', label: 'Sale cancellation' }
	];
	let product = $state<number | null>(page.url.searchParams.get('product') ? Number(page.url.searchParams.get('product')) : null);
	let productLabel = $state('');
	let type = $state('');
	let start = $state('');
	let end = $state('');
	const list = new ListState<StockLedgerRow>('reports/stock-ledger/', { product: page.url.searchParams.get('product') ?? undefined });
	list.load();

	async function loadProducts(q: string) {
		return (await api.get<Paginated<Product>>('products/', { search: q, page_size: 25 })).results;
	}
</script>

<svelte:head><title>Stock Ledger · Accounting</title></svelte:head>

<PageHeader title="Stock Ledger" description="Every stock movement, append-only. Opening/closing quantities are true historical balances.">
	{#snippet actions()}<ExportButtons path="reports/stock-ledger/" query={list.filters} />{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<div class="w-72">
			<label for="ledger-product" class="mb-1 block text-xs text-muted-foreground">Product</label>
			<Combobox id="ledger-product" bind:value={product} bind:label={productLabel} load={loadProducts} getLabel={(p: Product) => `${p.name} (${p.sku_code})`}
				placeholder="All products" onSelect={(p) => list.set('product', p?.id ?? undefined)} />
		</div>
		<Select class="w-48" bind:value={type} onchange={() => list.set('transaction_type', type)} options={TYPES} placeholder="All types" aria-label="Transaction type" />
		<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-9 w-40" onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-9 w-40" onchange={() => list.set('end_date', end)} /></label>
	</div>
	<StockLedgerTable rows={list.items} loading={list.loading} error={list.error} />
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
