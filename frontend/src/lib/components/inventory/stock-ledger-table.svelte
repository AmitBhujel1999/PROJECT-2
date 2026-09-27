<script lang="ts">
	import { qty, date as fmtDate } from '$lib/utilities/format';
	import type { StockLedgerRow } from '$lib/types';
	import TableState from '$lib/components/ui/table-state.svelte';

	let { rows, loading, error, showProduct = true }: { rows: StockLedgerRow[]; loading: boolean; error: string | null; showProduct?: boolean } = $props();

	function refHref(r: StockLedgerRow): string | null {
		if (r.reference_type === 'sale') return `/sales/${r.reference_id}`;
		if (r.reference_type === 'purchase') return `/purchases/${r.reference_id}`;
		if (r.reference_type === 'adjustment') return '/inventory/adjustments';
		return null;
	}
</script>

<div class="overflow-x-auto">
	<table class="table-base" data-testid="stock-ledger-table">
		<thead>
			<tr>
				<th>Date</th><th>Reference</th><th>Type</th>
				{#if showProduct}<th>Product</th>{/if}
				<th class="num">Opening</th><th class="num">In</th><th class="num">Out</th><th class="num">Closing</th><th>By</th>
			</tr>
		</thead>
		<tbody>
			<TableState {loading} {error} empty={!rows.length} colspan={showProduct ? 9 : 8} />
			{#each rows as r (r.id)}
				{@const href = refHref(r)}
				<tr>
					<td class="whitespace-nowrap">{fmtDate(r.date)}</td>
					<td class="whitespace-nowrap">{#if href}<a class="text-primary hover:underline" {href}>{r.reference_number}</a>{:else}{r.reference_number}{/if}</td>
					<td class="whitespace-nowrap">{r.transaction_type_display}</td>
					{#if showProduct}<td><a class="hover:underline" href="/products/{r.product_id}">{r.product_name}</a> <span class="text-xs text-muted-foreground">{r.sku_code}</span></td>{/if}
					<td class="num">{qty(r.opening_quantity)}</td>
					<td class="num text-emerald-700">{Number(r.quantity_in) ? qty(r.quantity_in) : ''}</td>
					<td class="num text-red-600">{Number(r.quantity_out) ? qty(r.quantity_out) : ''}</td>
					<td class="num font-medium">{qty(r.closing_quantity)}</td>
					<td class="text-xs text-muted-foreground">{r.created_by ?? ''}</td>
				</tr>
			{/each}
		</tbody>
	</table>
</div>
