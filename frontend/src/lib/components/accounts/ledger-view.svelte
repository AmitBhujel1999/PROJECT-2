<script lang="ts">
	import { ListState } from '$lib/stores/list.svelte';
	import { money, date as fmtDate } from '$lib/utilities/format';
	import type { LedgerEntry, PartyType } from '$lib/types';
	import { SIDES } from '$lib/components/parties/config';
	import Input from '$lib/components/ui/input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';

	let { type, partyId }: { type: PartyType; partyId: number } = $props();
	const side = $derived(SIDES[type]);

	let start = $state('');
	let end = $state('');
	// svelte-ignore state_referenced_locally
	const list = new ListState<LedgerEntry>(`${SIDES[type].api}/${partyId}/ledger/`);
	list.load();

	const x = $derived(list.extra as { opening_balance?: string; closing_balance?: string; total_debit?: string; total_credit?: string });

	function href(e: LedgerEntry) {
		return e.reference_type === 'document' ? `${side.docRoute}/${e.reference_id}` : `${side.payRoute}/${e.reference_id}`;
	}
	const path = $derived(`${side.api}/${partyId}/ledger/`);
</script>

<div class="flex flex-wrap items-end justify-between gap-2 border-b p-3">
	<div class="flex flex-wrap items-end gap-2">
		<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-8 w-40" onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-8 w-40" onchange={() => list.set('end_date', end)} /></label>
	</div>
	<div class="flex gap-2"><ExportButtons {path} query={list.filters} /></div>
</div>
<div class="grid grid-cols-2 gap-3 border-b bg-muted/30 p-3 text-sm sm:grid-cols-4">
	<div><p class="text-xs text-muted-foreground">Opening balance</p><p class="font-semibold tabular-nums">{money(x.opening_balance)}</p></div>
	<div><p class="text-xs text-muted-foreground">Total debit</p><p class="font-semibold tabular-nums">{money(x.total_debit)}</p></div>
	<div><p class="text-xs text-muted-foreground">Total credit</p><p class="font-semibold tabular-nums">{money(x.total_credit)}</p></div>
	<div><p class="text-xs text-muted-foreground">Closing balance</p><p class="font-semibold tabular-nums" data-testid="ledger-closing">{money(x.closing_balance)}</p></div>
</div>
<div class="overflow-x-auto">
	<table class="table-base" data-testid="ledger-table">
		<thead>
			<tr><th>Date</th><th>Reference</th><th>Type</th><th>Description</th><th class="num">Debit</th><th class="num">Credit</th><th class="num">Balance</th><th>Due date</th><th>Status</th></tr>
		</thead>
		<tbody>
			<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={9} emptyText="No transactions in this period." />
			{#each list.items as e, i (i + e.reference + e.transaction_type)}
				<tr class:opacity-60={e.status === 'CANCELLED'}>
					<td class="whitespace-nowrap">{fmtDate(e.date)}</td>
					<td class="whitespace-nowrap"><a class="text-primary hover:underline" href={href(e)}>{e.reference}</a></td>
					<td class="whitespace-nowrap">{e.transaction_type}</td>
					<td class="min-w-48 text-muted-foreground">{e.description}</td>
					<td class="num">{money(e.debit, true)}</td>
					<td class="num">{money(e.credit, true)}</td>
					<td class="num font-medium">{money(e.balance)}</td>
					<td class="whitespace-nowrap">{fmtDate(e.due_date)}</td>
					<td>{#if e.payment_status}<StatusBadge status={e.payment_status} />{/if}</td>
				</tr>
			{/each}
		</tbody>
	</table>
</div>
<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
<p class="px-3 pb-3 text-xs text-muted-foreground">
	{type === 'CUSTOMER' ? 'Debit = customer owes the business; Credit = payment received / credit.' : 'Credit = amount owed to the vendor; Debit = payment made / debit.'}
</p>
