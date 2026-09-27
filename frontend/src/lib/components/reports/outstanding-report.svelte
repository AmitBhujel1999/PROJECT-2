<script lang="ts">
	import { ListState } from '$lib/stores/list.svelte';
	import { money, date as fmtDate, today } from '$lib/utilities/format';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import PartyPicker from '$lib/components/parties/party-picker.svelte';

	interface Row { id: number; number: string; date: string; due_date: string; party_id: number; party_name: string; total_amount: string; paid: string; outstanding: string; days_overdue: number; payment_status: string }

	let { kind }: { kind: 'receivables' | 'payables' } = $props();
	// svelte-ignore state_referenced_locally
	const isRec = kind === 'receivables';
	// svelte-ignore state_referenced_locally
	const path = `reports/${kind}/`;
	const list = new ListState<Row, Record<string, any>>(path, { as_of: today() });
	list.load();
	let asOf = $state(today());
	let overdue = $state(false);
	let partyId = $state<number | null>(null);
	let partyLabel = $state('');
	const s = $derived(list.summary);
</script>

<PageHeader title={isRec ? 'Receivables Report' : 'Payables Report'} description="Open {isRec ? 'invoices' : 'bills'} with outstanding balance as of the selected date.">
	{#snippet actions()}<ExportButtons {path} query={list.filters} />{/snippet}
</PageHeader>

{#if s}
	<div class="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4">
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Open documents</p><p class="text-lg font-semibold">{s.count}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Document total</p><p class="text-lg font-semibold tabular-nums">{money(s.total)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Outstanding</p><p class="text-lg font-semibold tabular-nums">{money(s.outstanding)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Overdue</p><p class="text-lg font-semibold tabular-nums text-red-600">{money(s.overdue)}</p></div>
	</div>
{/if}

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<label class="grid gap-1 text-xs text-muted-foreground">As of<Input type="date" bind:value={asOf} class="h-9 w-40" onchange={() => list.set('as_of', asOf)} /></label>
		<div class="w-72"><label for="out-party" class="mb-1 block text-xs text-muted-foreground">{isRec ? 'Customer' : 'Vendor'}</label>
			<PartyPicker type={isRec ? 'CUSTOMER' : 'VENDOR'} id="out-party" bind:value={partyId} bind:label={partyLabel} onSelect={(p) => list.set('party', p?.id ?? '')} /></div>
		<label class="flex h-9 items-center gap-2 text-sm"><input type="checkbox" bind:checked={overdue} onchange={() => list.set('overdue', overdue ? 'true' : '')} /> Overdue only</label>
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>Number</th><th>Date</th><th>Due date</th><th>{isRec ? 'Customer' : 'Vendor'}</th><th class="num">Total</th><th class="num">Paid</th><th class="num">Outstanding</th><th class="num">Days overdue</th><th>Status</th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={9} emptyText="Nothing outstanding." />
				{#each list.items as r (r.id)}
					<tr>
						<td><a class="text-primary hover:underline" href="/{isRec ? 'sales' : 'purchases'}/{r.id}">{r.number}</a></td>
						<td>{fmtDate(r.date)}</td><td>{fmtDate(r.due_date)}</td>
						<td><a class="hover:underline" href="/{isRec ? 'customers' : 'vendors'}/{r.party_id}">{r.party_name}</a></td>
						<td class="num">{money(r.total_amount)}</td><td class="num">{money(r.paid, true)}</td>
						<td class="num font-medium">{money(r.outstanding)}</td>
						<td class="num" class:text-red-600={r.days_overdue > 0}>{r.days_overdue || '—'}</td>
						<td><StatusBadge status={r.payment_status} /></td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
