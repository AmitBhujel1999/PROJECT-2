<script lang="ts">
	import { Plus, BookOpen } from '@lucide/svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money, date as fmtDate } from '$lib/utilities/format';
	import type { JournalVoucherRow } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';

	const list = new ListState<JournalVoucherRow>('journal/vouchers/');
	list.load();
	let start = $state('');
	let end = $state('');
	let status = $state('');
</script>

<svelte:head><title>Journal Vouchers · Accounting</title></svelte:head>

<PageHeader title="Journal Vouchers" description="Dr/Cr adjustments: capital, loans, assets, depreciation, provisions, party adjustments.">
	{#snippet actions()}
		<Button variant="outline" href="/journal/ledgers"><BookOpen />Chart of Accounts</Button>
		{#if auth.can('journal.create')}<Button href="/journal/new" data-testid="new-jv"><Plus />New voucher</Button>{/if}
	{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<SearchInput placeholder="Number or narration…" onSearch={(v) => list.set('search', v)} />
		<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-9 w-40" onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-9 w-40" onchange={() => list.set('end_date', end)} /></label>
		<Select class="w-36" bind:value={status} onchange={() => list.set('status', status)} options={[{ value: 'ACTIVE', label: 'Active' }, { value: 'CANCELLED', label: 'Cancelled' }]} placeholder="Any status" aria-label="Status" />
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>Number</th><th>Date</th><th>Debit</th><th>Credit</th><th>Narration</th><th class="num">Amount</th><th>Status</th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={7} onRetry={() => list.load()} emptyText="No journal vouchers yet." />
				{#each list.items as v (v.id)}
					<tr class:opacity-60={v.status === 'CANCELLED'}>
						<td><a class="font-medium text-primary hover:underline" href="/journal/{v.id}">{v.voucher_number}</a></td>
						<td class="whitespace-nowrap">{fmtDate(v.date)}</td>
						<td>{v.accounts.debit.join(', ')}</td>
						<td>{v.accounts.credit.join(', ')}</td>
						<td class="max-w-80 truncate text-muted-foreground">{v.narration}</td>
						<td class="num font-medium">{money(v.total_amount)}</td>
						<td><StatusBadge status={v.status} /></td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
