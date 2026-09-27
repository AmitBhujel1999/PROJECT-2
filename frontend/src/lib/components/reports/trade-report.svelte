<script lang="ts">
	import { ListState } from '$lib/stores/list.svelte';
	import { money, qty, date as fmtDate } from '$lib/utilities/format';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import PartyPicker from '$lib/components/parties/party-picker.svelte';

	interface Row {
		id: number;
		date: string;
		number: string;
		party_id: number;
		party_name: string;
		items: number;
		quantity: string;
		gross_amount: string;
		discount: string;
		tax_amount: string;
		total_amount: string;
		balance_due: string;
		payment_status: string;
		status: string;
	}

	let { kind }: { kind: 'sales' | 'purchases' } = $props();
	// svelte-ignore state_referenced_locally
	const isSale = kind === 'sales';
	// svelte-ignore state_referenced_locally
	const path = `reports/${kind}/`;
	const list = new ListState<Row, Record<string, any>>(path);
	list.load();

	let start = $state('');
	let end = $state('');
	let number = $state('');
	let partyId = $state<number | null>(null);
	let partyLabel = $state('');
	let paymentStatus = $state('');
	let status = $state('ACTIVE');
	const s = $derived(list.summary);
	const cards = $derived(
		s
			? [
					[isSale ? 'Total invoices' : 'Total bills', String(s.total_documents)],
					[isSale ? 'Gross sales' : 'Gross purchases', money(s.gross_amount)],
					['Total discount', money(s.total_discount)],
					[isSale ? 'Taxable sales' : 'Taxable purchases', money(s.taxable_amount)],
					[isSale ? 'Total tax' : 'Input tax', money(s.tax_amount)],
					[isSale ? 'Net revenue' : 'Net purchases', money(s.net_amount)],
					[isSale ? 'Outstanding' : 'Outstanding payables', money(s.outstanding)]
				]
			: []
	);
</script>

<PageHeader title={isSale ? 'Sales Report' : 'Purchase Report'} description="Net = taxable amount after all discounts, excluding VAT. Cancelled documents are excluded unless selected.">
	{#snippet actions()}<ExportButtons {path} query={list.filters} />{/snippet}
</PageHeader>

<Card class="mb-4 no-print">
	<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-6">
		<label class="grid gap-1 text-xs text-muted-foreground">Start date<Input type="date" bind:value={start} onchange={() => list.set('start_date', start)} data-testid="report-start" /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">End date<Input type="date" bind:value={end} onchange={() => list.set('end_date', end)} data-testid="report-end" /></label>
		<div class="grid gap-1 text-xs text-muted-foreground lg:col-span-2">
			<label for="report-party">{isSale ? 'Customer' : 'Vendor'}</label>
			<PartyPicker type={isSale ? 'CUSTOMER' : 'VENDOR'} id="report-party" bind:value={partyId} bind:label={partyLabel} onSelect={(p) => list.set('party', p?.id ?? '')} />
		</div>
		<label class="grid gap-1 text-xs text-muted-foreground">{isSale ? 'Invoice number' : 'Bill number'}<Input bind:value={number} onchange={() => list.set('number', number)} placeholder="e.g. 000123" /></label>
		<div class="grid grid-cols-2 gap-2">
			<label class="grid gap-1 text-xs text-muted-foreground">Payment<Select bind:value={paymentStatus} onchange={() => list.set('payment_status', paymentStatus)} placeholder="Any" options={[{ value: 'PAID', label: 'Paid' }, { value: 'PARTIAL', label: 'Partial' }, { value: 'UNPAID', label: 'Unpaid' }]} /></label>
			<label class="grid gap-1 text-xs text-muted-foreground">Status<Select bind:value={status} onchange={() => list.set('status', status)} options={[{ value: 'ACTIVE', label: 'Active' }, { value: 'CANCELLED', label: 'Cancelled' }, { value: 'ALL', label: 'All' }]} /></label>
		</div>
	</div>
</Card>

<div class="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4 xl:grid-cols-7" data-testid="report-summary">
	{#each cards as [label, value] (label)}
		<div class="rounded-xl border bg-card p-3 shadow-xs"><p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</p><p class="mt-1 font-semibold tabular-nums">{value}</p></div>
	{/each}
</div>

<Card bodyClass="p-0">
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="report-table">
			<thead>
				<tr><th>Date</th><th>{isSale ? 'Invoice #' : 'Bill #'}</th><th>{isSale ? 'Customer' : 'Vendor'}</th><th class="num">Items</th><th class="num">Quantity</th><th class="num">Gross</th><th class="num">Discount</th><th class="num">Tax</th><th class="num">Total</th><th>Payment</th></tr>
			</thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={10} />
				{#each list.items as r (r.id)}
					<tr class:opacity-60={r.status === 'CANCELLED'}>
						<td class="whitespace-nowrap">{fmtDate(r.date)}</td>
						<td><a class="text-primary hover:underline" href="/{kind}/{r.id}">{r.number}</a></td>
						<td>{r.party_name}</td>
						<td class="num">{r.items}</td>
						<td class="num">{qty(r.quantity)}</td>
						<td class="num">{money(r.gross_amount)}</td>
						<td class="num">{money(r.discount, true)}</td>
						<td class="num">{money(r.tax_amount)}</td>
						<td class="num font-medium">{money(r.total_amount)}</td>
						<td><StatusBadge status={r.status === 'CANCELLED' ? 'CANCELLED' : r.payment_status} /></td>
					</tr>
				{/each}
			</tbody>
			{#if s && list.items.length}
				<tfoot><tr><td colspan="5">TOTAL ({s.total_documents})</td><td class="num">{money(s.gross_amount)}</td><td class="num">{money(s.total_discount)}</td><td class="num">{money(s.tax_amount)}</td><td class="num">{money(s.total_amount)}</td><td></td></tr></tfoot>
			{/if}
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
