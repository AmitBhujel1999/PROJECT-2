<script lang="ts">
	import { page } from '$app/state';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { Pencil } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money, qty, date as fmtDate, today } from '$lib/utilities/format';
	import type { Product, StockLedgerRow } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import ProductFormDialog from '$lib/components/products/product-form-dialog.svelte';
	import StockLedgerTable from '$lib/components/inventory/stock-ledger-table.svelte';

	const id = $derived(Number(page.params.id));
	const client = useQueryClient();
	const product = createQuery(() => ({ queryKey: ['product', id], queryFn: () => api.get<Product>(`products/${id}/`) }));

	let asOf = $state(today());
	const stock = createQuery(() => ({
		queryKey: ['product-stock', id, asOf],
		queryFn: () => api.get<{ current_stock: string; stock_as_of: string }>(`inventory/${id}/`, { as_of: asOf })
	}));

	const ledger = new ListState<StockLedgerRow>('inventory/ledger/', { product: page.params.id });
	ledger.load();

	let editOpen = $state(false);
	const p = $derived(product.data);
</script>

<svelte:head><title>{p?.name ?? 'Product'} · Accounting</title></svelte:head>

{#if p}
	<PageHeader title={p.name} description="SKU {p.sku_code} · {p.unit_display}" back={{ href: '/products', label: 'Products' }}>
		{#snippet actions()}
			<StatusBadge status={p.is_active ? 'ACTIVE' : 'INACTIVE'} />
			{#if auth.can('products.manage')}<Button variant="outline" onclick={() => (editOpen = true)}><Pencil />Edit</Button>{/if}
		{/snippet}
	</PageHeader>

	<div class="grid gap-4 lg:grid-cols-3">
		<Card title="Pricing">
			<dl class="grid grid-cols-2 gap-y-2 text-sm">
				<dt class="text-muted-foreground">Purchase price</dt><dd class="num">{money(p.purchase_price)}</dd>
				<dt class="text-muted-foreground">Selling price</dt><dd class="num">{money(p.selling_price)}</dd>
				<dt class="text-muted-foreground">Tax rate</dt><dd class="num">{p.tax_rate}%</dd>
			</dl>
		</Card>
		<Card title="Stock">
			<dl class="grid grid-cols-2 gap-y-2 text-sm">
				<dt class="text-muted-foreground">Current stock</dt><dd class="num font-semibold" data-testid="product-current-stock">{qty(p.current_stock)} {p.unit_display}</dd>
				<dt class="text-muted-foreground">Opening stock</dt><dd class="num">{qty(p.opening_stock)}</dd>
				<dt class="text-muted-foreground">Reorder level</dt><dd class="num">{qty(p.reorder_level)}</dd>
			</dl>
		</Card>
		<Card title="Historical stock">
			<div class="flex items-end gap-2">
				<label class="grid flex-1 gap-1 text-sm">As of date<Input type="date" bind:value={asOf} /></label>
				<p class="pb-2 text-right text-lg font-semibold tabular-nums">{stock.data ? qty(stock.data.stock_as_of) : '…'}</p>
			</div>
		</Card>
	</div>

	<Card title="Stock ledger" class="mt-4" bodyClass="p-0">
		<StockLedgerTable rows={ledger.items} loading={ledger.loading} error={ledger.error} showProduct={false} />
		<Pagination page={ledger.page} totalPages={ledger.totalPages} count={ledger.count} pageSize={ledger.pageSize} onPage={ledger.setPage} onPageSize={ledger.setPageSize} />
	</Card>

	<ProductFormDialog bind:open={editOpen} product={p} onSaved={() => client.invalidateQueries({ queryKey: ['product', id] })} />
{:else if product.isError}
	<h1 class="text-xl font-semibold">Not available</h1>
	<p class="mt-1 text-destructive">{product.error.message}</p>
{/if}
