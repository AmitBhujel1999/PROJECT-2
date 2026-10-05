<script lang="ts">
	import { Plus } from '@lucide/svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money, date as fmtDate, PAYMENT_METHODS } from '$lib/utilities/format';
	import type { PaymentDoc } from '$lib/types';
	import { PAY_SIDES, type PayKind } from './config';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import MCard from '$lib/components/mobile/m-card.svelte';
	import MState from '$lib/components/mobile/m-state.svelte';
	import { phone } from '$lib/stores/viewport.svelte';

	let { kind }: { kind: PayKind } = $props();
	// svelte-ignore state_referenced_locally
	const side = PAY_SIDES[kind];
	const list = new ListState<PaymentDoc>(`${side.api}/`);
	list.load();
	let method = $state('');
	let start = $state('');
	let end = $state('');
	let unallocated = $state(false);
</script>

<PageHeader title={side.title} description="Money {kind === 'receipt' ? 'received from customers' : 'paid to vendors'}. Unallocated amounts are advances.">
	{#snippet actions()}
		{#if auth.can(side.createPerm)}<Button href="{side.route}/new" data-testid="new-payment"><Plus />New {side.label.toLowerCase()}</Button>{/if}
	{/snippet}
</PageHeader>

{#if phone.current}
	<div class="mb-2 grid gap-2">
		<SearchInput placeholder="Number, {side.partyLabel.toLowerCase()}, reference…" onSearch={(v) => list.set('search', v)} /></div>
	<MState loading={list.loading} error={list.error} empty={!list.items.length} onRetry={() => list.load()} />
	<div>
		{#each list.items as r (r.id)}
			<MCard href="{side.route}/{r.id}" title={r.number} subtitle={r.party_name}
				meta="{fmtDate(r.date)} · {r.payment_method_display}{r.reference_number ? ` · ${r.reference_number}` : ''}"
				amount={money(r.amount)} muted={r.status === 'CANCELLED'}>
				{#snippet badge()}
					{#if r.status === 'CANCELLED'}<StatusBadge status="CANCELLED" />
					{:else if Number(r.unallocated_amount) > 0}<span class="text-[11px] font-medium text-amber-700">Advance {money(r.unallocated_amount)}</span>
					{:else}<span class="text-[11px] text-muted-foreground">Allocated</span>{/if}
				{/snippet}
			</MCard>
		{/each}
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
{:else}
<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<SearchInput placeholder="Number, {side.partyLabel.toLowerCase()}, reference…" onSearch={(v) => list.set('search', v)} />
		<Select class="w-40" bind:value={method} onchange={() => list.set('payment_method', method)} options={PAYMENT_METHODS} placeholder="Any method" aria-label="Payment method" />
		<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-9 w-40" onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-9 w-40" onchange={() => list.set('end_date', end)} /></label>
		<label class="flex h-9 items-center gap-2 text-sm"><input type="checkbox" bind:checked={unallocated} onchange={() => list.set('unallocated', unallocated ? 'true' : '')} /> Unallocated only</label>
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>Number</th><th>Date</th><th>{side.partyLabel}</th><th>Method</th><th>Reference</th><th class="num">Amount</th><th class="num">Allocated</th><th class="num">Unallocated</th><th>Status</th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={9} />
				{#each list.items as r (r.id)}
					<tr>
						<td><a class="font-medium text-primary hover:underline" href="{side.route}/{r.id}">{r.number}</a></td>
						<td class="whitespace-nowrap">{fmtDate(r.date)}</td>
						<td>{r.party_name}</td>
						<td>{r.payment_method_display}</td>
						<td class="text-muted-foreground">{r.reference_number}</td>
						<td class="num font-medium">{money(r.amount)}</td>
						<td class="num">{money(r.allocated_amount, true)}</td>
						<td class="num" class:text-amber-700={Number(r.unallocated_amount) > 0}>{money(r.unallocated_amount, true)}</td>
						<td><StatusBadge status={r.status} /></td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
{/if}
