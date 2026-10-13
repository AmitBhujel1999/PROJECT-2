<script lang="ts">
	import { Plus } from '@lucide/svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money, date as fmtDate } from '$lib/utilities/format';
	import type { Contra } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import AccountSelect from '$lib/components/cashbank/account-select.svelte';

	const list = new ListState<Contra>('cash-bank/contras/');
	list.load();
	let account = $state('');
	let start = $state('');
	let end = $state('');
</script>

<svelte:head><title>Contra · Accounting</title></svelte:head>

<PageHeader title="Contra entries" description="Money moved between your own accounts: cash deposited in the bank, cash withdrawn, wallet top-ups." back={{ href: '/cash-bank', label: 'Cash & Bank' }}>
	{#snippet actions()}
		{#if auth.can('contra.create')}<Button href="/cash-bank/contra/new" data-testid="new-contra"><Plus />New contra</Button>{/if}
	{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<SearchInput placeholder="Number, account, notes…" onSearch={(v) => list.set('search', v)} />
		<AccountSelect class="w-44" bind:value={account} onchange={() => list.set('account', account)} placeholder="Any account" aria-label="Account" />
		<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-9 w-40" onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-9 w-40" onchange={() => list.set('end_date', end)} /></label>
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>Number</th><th>Date</th><th>From</th><th>To</th><th>Notes</th><th class="num">Amount</th><th>Status</th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={7} onRetry={() => list.load()} />
				{#each list.items as c (c.id)}
					<tr class:opacity-60={c.status === 'CANCELLED'}>
						<td><a class="font-medium text-primary hover:underline" href="/cash-bank/contra/{c.id}">{c.contra_number}</a></td>
						<td class="whitespace-nowrap">{fmtDate(c.date)}</td>
						<td>{c.from_account_name}</td>
						<td>{c.to_account_name}</td>
						<td class="text-muted-foreground">{c.notes}</td>
						<td class="num font-medium">{money(c.amount)}</td>
						<td><StatusBadge status={c.status} /></td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
