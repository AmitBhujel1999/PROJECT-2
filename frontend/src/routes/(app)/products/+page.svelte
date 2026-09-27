<script lang="ts">
	import { Plus, Pencil, Power } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money, qty, UNITS } from '$lib/utilities/format';
	import type { Product } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';
	import ProductFormDialog from '$lib/components/products/product-form-dialog.svelte';

	const list = new ListState<Product>('products/', { is_active: 'true' });
	list.load();

	let formOpen = $state(false);
	let editing = $state<Product | null>(null);
	let confirmOpen = $state(false);
	let target = $state<Product | null>(null);
	let active = $state('true');
	let unit = $state('');
	const canManage = $derived(auth.can('products.manage'));

	async function toggleActive() {
		if (!target) return;
		const res = await api.post<Product>(`products/${target.id}/${target.is_active ? 'deactivate' : 'activate'}/`);
		toast.success(res.message);
		list.load();
	}
</script>

<svelte:head><title>Products · Accounting</title></svelte:head>

<PageHeader title="Products" description="Items you buy and sell. Products with history are deactivated, never deleted.">
	{#snippet actions()}
		{#if canManage}
			<Button onclick={() => ((editing = null), (formOpen = true))} data-testid="new-product"><Plus />New product</Button>
		{/if}
	{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-center gap-2 border-b p-3">
		<SearchInput placeholder="Search name or SKU…" onSearch={(v) => list.set('search', v)} />
		<Select class="w-36" bind:value={active} onchange={() => list.set('is_active', active)} aria-label="Status filter"
			options={[{ value: 'true', label: 'Active' }, { value: 'false', label: 'Inactive' }]} placeholder="All" />
		<Select class="w-32" bind:value={unit} onchange={() => list.set('unit', unit)} aria-label="Unit filter" options={UNITS} placeholder="All units" />
	</div>
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="products-table">
			<thead>
				<tr>
					<th>SKU</th><th>Name</th><th>Unit</th><th class="num">Stock</th><th class="num">Purchase</th><th class="num">Selling</th><th class="num">Tax %</th><th>Status</th>
					{#if canManage}<th class="text-right">Actions</th>{/if}
				</tr>
			</thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={9} onRetry={() => list.load()} />
				{#each list.items as p (p.id)}
					<tr>
						<td class="font-mono text-xs">{p.sku_code}</td>
						<td><a href="/products/{p.id}" class="font-medium text-primary hover:underline">{p.name}</a></td>
						<td>{p.unit_display}</td>
						<td class="num" class:text-red-600={Number(p.current_stock) <= Number(p.reorder_level)}>{qty(p.current_stock)}</td>
						<td class="num">{money(p.purchase_price)}</td>
						<td class="num">{money(p.selling_price)}</td>
						<td class="num">{p.tax_rate}</td>
						<td><StatusBadge status={p.is_active ? 'ACTIVE' : 'INACTIVE'} /></td>
						{#if canManage}
							<td class="whitespace-nowrap text-right">
								<Button size="sm" variant="ghost" onclick={() => ((editing = p), (formOpen = true))} aria-label="Edit {p.name}"><Pencil /></Button>
								<Button size="sm" variant="ghost" onclick={() => ((target = p), (confirmOpen = true))} aria-label="{p.is_active ? 'Deactivate' : 'Activate'} {p.name}"><Power /></Button>
							</td>
						{/if}
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>

<ProductFormDialog bind:open={formOpen} product={editing} onSaved={() => list.load()} />
<ConfirmDialog
	bind:open={confirmOpen}
	title={target?.is_active ? 'Deactivate product?' : 'Activate product?'}
	message={target?.is_active ? `${target?.name} will no longer be available for new transactions. History is kept.` : `${target?.name} will be available again.`}
	confirmLabel={target?.is_active ? 'Deactivate' : 'Activate'}
	destructive={target?.is_active}
	onConfirm={toggleActive}
/>
