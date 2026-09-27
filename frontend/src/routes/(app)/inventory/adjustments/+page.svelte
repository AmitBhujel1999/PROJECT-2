<script lang="ts">
	import { Plus } from '@lucide/svelte';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { qty, date as fmtDate, today, ADJUSTMENT_REASONS } from '$lib/utilities/format';
	import type { Paginated, Product } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Dialog from '$lib/components/ui/dialog.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Combobox from '$lib/components/ui/combobox.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';

	interface Adjustment {
		id: number;
		adjustment_number: string;
		product: number;
		product_name: string;
		sku_code: string;
		date: string;
		quantity: string;
		reason: string;
		reason_display: string;
		notes: string;
		stock_before: string;
		stock_after: string;
		created_by_name: string;
	}

	const list = new ListState<Adjustment>('inventory/adjustments/');
	list.load();

	let open = $state(false);
	let productId = $state<number | null>(null);
	let productLabel = $state('');
	let current = $state<string | null>(null);
	let direction = $state<'-' | '+'>('-');
	let amount = $state('');
	let reason = $state('DAMAGED');
	let date = $state(today());
	let notes = $state('');
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);

	async function loadProducts(q: string) {
		return (await api.get<Paginated<Product>>('products/', { search: q, is_active: 'true', page_size: 25 })).results;
	}

	async function save(e: SubmitEvent) {
		e.preventDefault();
		errors = {};
		if (!productId) {
			errors.product = 'Select a product.';
			return;
		}
		busy = true;
		try {
			const res = await api.post('inventory/adjustments/', { product: productId, date, quantity: `${direction}${amount}`, reason, notes });
			toast.success(`✓ ${res.message}`);
			open = false;
			amount = '';
			notes = '';
			productId = null;
			productLabel = '';
			list.load();
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError) toast.error(`✕ ${err.message}`);
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Stock Adjustments · Accounting</title></svelte:head>

<PageHeader title="Stock Adjustments" description="Damaged, expired, lost or counted stock. Every adjustment is posted to the stock ledger and audit log.">
	{#snippet actions()}
		{#if auth.can('inventory.adjust')}<Button onclick={() => (open = true)} data-testid="new-adjustment"><Plus />New adjustment</Button>{/if}
	{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="border-b p-3"><SearchInput placeholder="Search number, product, notes…" onSearch={(v) => list.set('search', v)} /></div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>Number</th><th>Date</th><th>Product</th><th class="num">Before</th><th class="num">Adjustment</th><th class="num">After</th><th>Reason</th><th>Notes</th><th>By</th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={9} />
				{#each list.items as a (a.id)}
					<tr>
						<td class="font-medium">{a.adjustment_number}</td>
						<td>{fmtDate(a.date)}</td>
						<td><a class="hover:underline" href="/products/{a.product}">{a.product_name}</a> <span class="text-xs text-muted-foreground">{a.sku_code}</span></td>
						<td class="num">{qty(a.stock_before)}</td>
						<td class="num font-semibold" class:text-red-600={a.quantity.startsWith('-')} class:text-emerald-700={!a.quantity.startsWith('-')}>{a.quantity.startsWith('-') ? '' : '+'}{qty(a.quantity)}</td>
						<td class="num">{qty(a.stock_after)}</td>
						<td>{a.reason_display}</td>
						<td class="text-muted-foreground">{a.notes}</td>
						<td class="text-xs">{a.created_by_name}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>

<Dialog bind:open title="New stock adjustment" description="Adjustments cannot make stock negative.">
	<form id="adj-form" class="grid gap-4" onsubmit={save}>
		<Field label="Product" for="adj-product" required error={errors.product}>
			<Combobox id="adj-product" bind:value={productId} bind:label={productLabel} load={loadProducts} getLabel={(p: Product) => `${p.name} (${p.sku_code})`}
				onSelect={(p) => (current = p?.current_stock ?? null)} invalid={!!errors.product} />
			{#if current !== null}<p class="text-xs text-muted-foreground">Current stock: <b>{qty(current)}</b></p>{/if}
		</Field>
		<div class="grid grid-cols-[8rem_1fr] gap-3">
			<Field label="Direction" for="adj-dir">
				<Select id="adj-dir" bind:value={direction} options={[{ value: '-', label: 'Decrease (−)' }, { value: '+', label: 'Increase (+)' }]} />
			</Field>
			<Field label="Quantity" for="adj-qty" required error={errors.quantity}>
				<Input id="adj-qty" type="number" min="0.001" step="any" bind:value={amount} required class="text-right" />
			</Field>
		</div>
		<div class="grid grid-cols-2 gap-3">
			<Field label="Reason" for="adj-reason" required><Select id="adj-reason" bind:value={reason} options={ADJUSTMENT_REASONS} /></Field>
			<Field label="Date" for="adj-date" required error={errors.date}><Input id="adj-date" type="date" bind:value={date} required /></Field>
		</div>
		<Field label="Notes" for="adj-notes" error={errors.notes} hint="Required when the reason is Other."><Textarea id="adj-notes" bind:value={notes} rows={2} maxlength={255} /></Field>
	</form>
	{#snippet footer()}
		<Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
		<Button type="submit" form="adj-form" loading={busy}>Post adjustment</Button>
	{/snippet}
</Dialog>
