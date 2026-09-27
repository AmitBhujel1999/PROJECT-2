<script lang="ts">
	import { Plus } from '@lucide/svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money, qty, date as fmtDate } from '$lib/utilities/format';
	import type { TradeDoc } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';

	let { kind }: { kind: 'sale' | 'purchase' } = $props();
	// svelte-ignore state_referenced_locally
	const isSale = kind === 'sale';
	const path = isSale ? 'sales' : 'purchases';
	const list = new ListState<TradeDoc>(`${path}/`, { status: 'ACTIVE' });
	list.load();

	let start = $state('');
	let end = $state('');
	let paymentStatus = $state('');
	let status = $state('ACTIVE');
</script>

<PageHeader title={isSale ? 'Sales' : 'Purchases'} description={isSale ? 'Customer invoices' : 'Vendor bills'}>
	{#snippet actions()}
		{#if auth.can(isSale ? 'sales.create' : 'purchases.create')}
			<Button href="/{path}/new" data-testid="new-doc"><Plus />{isSale ? 'New sale' : 'New purchase'}</Button>
		{/if}
	{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<SearchInput placeholder={isSale ? 'Invoice no., customer, phone…' : 'Bill no., vendor…'} onSearch={(v) => list.set('search', v)} />
		<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-9 w-40" onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-9 w-40" onchange={() => list.set('end_date', end)} /></label>
		<Select class="w-36" bind:value={paymentStatus} onchange={() => list.set('payment_status', paymentStatus)} aria-label="Payment status"
			options={[{ value: 'PAID', label: 'Paid' }, { value: 'PARTIAL', label: 'Partial' }, { value: 'UNPAID', label: 'Unpaid' }]} placeholder="Any payment" />
		<Select class="w-36" bind:value={status} onchange={() => list.set('status', status)} aria-label="Status"
			options={[{ value: 'ACTIVE', label: 'Active' }, { value: 'CANCELLED', label: 'Cancelled' }]} placeholder="All" />
	</div>
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="doc-list">
			<thead>
				<tr>
					<th>{isSale ? 'Invoice' : 'Bill'}</th><th>Date</th><th>Due</th><th>{isSale ? 'Customer' : 'Vendor'}</th>
					<th class="num">Items</th><th class="num">Qty</th><th class="num">Total</th><th class="num">Balance</th><th>Payment</th><th>Status</th>
				</tr>
			</thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={10} />
				{#each list.items as d (d.id)}
					<tr>
						<td class="whitespace-nowrap">
							<a class="font-medium text-primary hover:underline" href="/{path}/{d.id}">{isSale ? d.invoice_number : d.bill_number}</a>
							{#if !isSale && d.vendor_bill_number}<div class="text-[11px] text-muted-foreground">{d.vendor_bill_number}</div>{/if}
						</td>
						<td class="whitespace-nowrap">{fmtDate(d.date)}</td>
						<td class="whitespace-nowrap">{fmtDate(d.due_date)}</td>
						<td>{isSale ? d.customer_name : d.vendor_name}</td>
						<td class="num">{d.item_count}</td>
						<td class="num">{qty(d.total_quantity)}</td>
						<td class="num font-medium">{money(d.total_amount)}</td>
						<td class="num">{money(d.balance_due, true)}</td>
						<td><StatusBadge status={d.payment_status} /></td>
						<td><StatusBadge status={d.status} /></td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
