<script lang="ts">
	import { page } from '$app/state';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { Pencil, Plus } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money, date as fmtDate } from '$lib/utilities/format';
	import type { Party, PartyType, TradeDoc, PaymentDoc } from '$lib/types';
	import { SIDES } from './config';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Tabs from '$lib/components/ui/tabs.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import PartyFormDialog from './party-form-dialog.svelte';
	import LedgerView from '$lib/components/accounts/ledger-view.svelte';
	import StatementView from '$lib/components/accounts/statement-view.svelte';
	import PartyAgingView from '$lib/components/accounts/party-aging-view.svelte';

	let { type, id }: { type: PartyType; id: number } = $props();
	// svelte-ignore state_referenced_locally
	const side = SIDES[type];
	const client = useQueryClient();

	const party = createQuery(() => ({ queryKey: ['party', id], queryFn: () => api.get<Party>(`${side.api}/${id}/`) }));
	const summary = createQuery(() => ({ queryKey: ['party-summary', id], queryFn: () => api.get<Record<string, any>>(`${side.api}/${id}/summary/`) }));

	const finance = $derived(auth.can('ledgers.view'));
	let tab = $state(page.url.searchParams.get('tab') ?? 'documents');
	const tabs = $derived([
		{ value: 'documents', label: type === 'CUSTOMER' ? 'Sales' : 'Purchases' },
		...(auth.can(side.payPerm) ? [{ value: 'payments', label: type === 'CUSTOMER' ? 'Receipts' : 'Payments' }] : []),
		...(finance
			? [
					{ value: 'ledger', label: 'Ledger' },
					{ value: 'statement', label: 'Statement' },
					{ value: 'aging', label: 'Aging' }
				]
			: [])
	]);

	// svelte-ignore state_referenced_locally
	const docs = new ListState<TradeDoc>(`${side.docApi}/`, { [type === 'CUSTOMER' ? 'customer' : 'vendor']: String(id), status: '' });
	docs.load();
	// svelte-ignore state_referenced_locally
	const pays = new ListState<PaymentDoc>(`${side.payApi}/`, { party: String(id) });
	$effect(() => {
		if (tab === 'payments' && !pays.items.length && !pays.loading) pays.load();
	});

	let editOpen = $state(false);
	const p = $derived(party.data);
	const s = $derived(summary.data);
</script>

{#if p}
	<PageHeader title={p.name} description={[p.phone, p.pan_vat_no && `PAN/VAT ${p.pan_vat_no}`, p.credit_terms_display].filter(Boolean).join(' · ')} back={{ href: side.route, label: side.plural }}>
		{#snippet actions()}
			<StatusBadge status={p.is_active ? 'ACTIVE' : 'INACTIVE'} />
			{#if auth.can(type === 'CUSTOMER' ? 'sales.create' : 'purchases.create')}
				<Button variant="outline" href="{side.docRoute}/new?party={p.id}"><Plus />New {type === 'CUSTOMER' ? 'sale' : 'purchase'}</Button>
			{/if}
			{#if auth.can(type === 'CUSTOMER' ? 'receipts.create' : 'payments.create')}
				<Button variant="outline" href="{side.payRoute}/new?party={p.id}"><Plus />{side.payLabel}</Button>
			{/if}
			{#if auth.can('parties.manage')}<Button variant="outline" onclick={() => (editOpen = true)}><Pencil />Edit</Button>{/if}
		{/snippet}
	</PageHeader>

	{#if s}
		<div class="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4 xl:grid-cols-7" data-testid="party-summary">
			{#each [
				[side.docTotalLabel, money(s.total_documents)],
				[side.payTotalLabel, money(s.total_payments)],
				['Outstanding', money(s.outstanding)],
				['Overdue', money(s.overdue)],
				['Current', money(s.current)],
				['Advance / unallocated', money(s.unallocated)],
				[type === 'CUSTOMER' ? 'Credit limit' : 'Last transaction', type === 'CUSTOMER' ? (Number(s.credit_limit) ? money(s.credit_limit) : 'No limit') : fmtDate(s.last_transaction_date) || '—']
			] as [label, value] (label)}
				<div class="rounded-xl border bg-card p-3 shadow-xs">
					<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
					<p class="mt-1 font-semibold tabular-nums" class:text-red-600={label === 'Overdue' && Number(s.overdue) > 0}>{value}</p>
				</div>
			{/each}
		</div>
		{#if type === 'CUSTOMER'}
			<p class="mb-4 text-xs text-muted-foreground">Last transaction: {fmtDate(s.last_transaction_date) || '—'} · Net balance: {money(s.net_balance)}{#if s.available_credit !== null} · Available credit: {money(s.available_credit)}{/if}</p>
		{/if}
	{/if}

	<Tabs {tabs} bind:value={tab} />
	<Card bodyClass="p-0">
		{#if tab === 'documents'}
			<div class="overflow-x-auto">
				<table class="table-base">
					<thead><tr><th>{side.docLabel}</th><th>Date</th><th>Due</th><th class="num">Total</th><th class="num">Balance</th><th>Payment</th><th>Status</th></tr></thead>
					<tbody>
						<TableState loading={docs.loading} error={docs.error} empty={!docs.items.length} colspan={7} />
						{#each docs.items as d (d.id)}
							<tr>
								<td><a class="text-primary hover:underline" href="{side.docRoute}/{d.id}">{d[side.docNumberKey]}</a></td>
								<td>{fmtDate(d.date)}</td><td>{fmtDate(d.due_date)}</td>
								<td class="num">{money(d.total_amount)}</td><td class="num">{money(d.balance_due)}</td>
								<td><StatusBadge status={d.payment_status} /></td><td><StatusBadge status={d.status} /></td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			<Pagination page={docs.page} totalPages={docs.totalPages} count={docs.count} pageSize={docs.pageSize} onPage={docs.setPage} onPageSize={docs.setPageSize} />
		{:else if tab === 'payments'}
			<div class="overflow-x-auto">
				<table class="table-base">
					<thead><tr><th>{side.payLabel}</th><th>Date</th><th>Method</th><th class="num">Amount</th><th class="num">Unallocated</th><th>Status</th></tr></thead>
					<tbody>
						<TableState loading={pays.loading} error={pays.error} empty={!pays.items.length} colspan={6} />
						{#each pays.items as r (r.id)}
							<tr>
								<td><a class="text-primary hover:underline" href="{side.payRoute}/{r.id}">{r.number}</a></td>
								<td>{fmtDate(r.date)}</td><td>{r.payment_method_display}</td>
								<td class="num">{money(r.amount)}</td><td class="num">{money(r.unallocated_amount, true)}</td>
								<td><StatusBadge status={r.status} /></td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			<Pagination page={pays.page} totalPages={pays.totalPages} count={pays.count} pageSize={pays.pageSize} onPage={pays.setPage} onPageSize={pays.setPageSize} />
		{:else if tab === 'ledger'}
			<LedgerView {type} partyId={id} />
		{:else if tab === 'statement'}
			<StatementView {type} partyId={id} />
		{:else if tab === 'aging'}
			<PartyAgingView {type} partyId={id} />
		{/if}
	</Card>

	<PartyFormDialog bind:open={editOpen} {type} party={p} onSaved={() => client.invalidateQueries({ queryKey: ['party', id] })} />
{:else if party.isError}
	<h1 class="text-xl font-semibold">Not available</h1>
	<p class="mt-1 text-destructive">{party.error.message}</p>
{/if}
