<script lang="ts">
	import { ListState } from '$lib/stores/list.svelte';
	import { money, today } from '$lib/utilities/format';
	import type { AgingBucket, AgingRow, PartyType } from '$lib/types';
	import { SIDES } from '$lib/components/parties/config';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';

	let { type }: { type: PartyType } = $props();
	const side = $derived(SIDES[type]);
	const path = $derived(type === 'CUSTOMER' ? 'reports/receivables-aging/' : 'reports/payables-aging/');
	let asOf = $state(today());
	// svelte-ignore state_referenced_locally
	const list = new ListState<AgingRow>(type === 'CUSTOMER' ? 'reports/receivables-aging/' : 'reports/payables-aging/', { as_of: today() });
	list.load();

	const buckets = $derived((list.extra.buckets ?? []) as AgingBucket[]);
	const totals = $derived((list.extra.totals ?? {}) as Record<string, string>);
</script>

<PageHeader
	title={type === 'CUSTOMER' ? 'Accounts Receivable Aging' : 'Accounts Payable Aging'}
	description="Outstanding balances by days past due date as of the selected date. Advances are shown separately and never counted as overdue."
>
	{#snippet actions()}<ExportButtons {path} query={list.filters} />{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<label class="grid gap-1 text-xs text-muted-foreground">As of date
			<Input type="date" bind:value={asOf} class="h-9 w-44" onchange={() => list.set('as_of', asOf)} data-testid="aging-as-of" />
		</label>
		<SearchInput placeholder="Search {side.label.toLowerCase()}…" onSearch={(v) => list.set('search', v)} />
	</div>
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="aging-table">
			<thead>
				<tr>
					<th>{side.label}</th>
					{#each buckets as b (b.key)}<th class="num">{b.label.replace(' Days', '')}</th>{/each}
					<th class="num">Total</th><th class="num">Advances</th><th class="num">Net balance</th>
				</tr>
			</thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={10} emptyText="Nothing outstanding." />
				{#each list.items as r (r.party_id)}
					<tr>
						<td><a class="font-medium text-primary hover:underline" href="{side.route}/{r.party_id}?tab=aging">{r.party_name}</a></td>
						{#each buckets as b (b.key)}<td class="num" class:text-red-600={b.key !== 'current' && Number(r[b.key]) > 0}>{money(r[b.key] as string, true)}</td>{/each}
						<td class="num font-semibold">{money(r.total)}</td>
						<td class="num">{money(r.unallocated, true)}</td>
						<td class="num">{money(r.net_balance)}</td>
					</tr>
				{/each}
			</tbody>
			{#if list.items.length}
				<tfoot>
					<tr>
						<td>TOTAL</td>
						{#each buckets as b (b.key)}<td class="num">{money(totals[b.key])}</td>{/each}
						<td class="num" data-testid="aging-total">{money(totals.total)}</td><td class="num">{money(totals.unallocated)}</td><td class="num">{money(totals.net_balance)}</td>
					</tr>
				</tfoot>
			{/if}
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
