<script lang="ts">
	import { Plus } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money, date as fmtDate, PAYMENT_METHODS } from '$lib/utilities/format';
	import type { Expense, ExpenseCategory, Paginated } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';

	const list = new ListState<Expense>('expenses/');
	list.load();
	let categories = $state<{ value: number; label: string }[]>([]);
	api.get<Paginated<ExpenseCategory>>('expense-categories/', { page_size: 100 }).then((d) => {
		categories = d.results.map((c) => ({ value: c.id, label: c.name }));
	});
	let category = $state('');
	let method = $state('');
	let status = $state('');
	let start = $state('');
	let end = $state('');
</script>

<svelte:head><title>Expenses · Accounting</title></svelte:head>

<PageHeader title="Expenses" description="Day-to-day business costs paid at the time they are recorded. Expenses are cancelled, never deleted.">
	{#snippet actions()}
		{#if auth.can('expenses.create')}<Button href="/expenses/new" data-testid="new-expense"><Plus />New expense</Button>{/if}
	{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<SearchInput placeholder="Number, description, paid to, reference…" onSearch={(v) => list.set('search', v)} />
		<Select class="w-48" bind:value={category} onchange={() => list.set('category', category)} options={categories} placeholder="Any category" aria-label="Category" />
		<Select class="w-40" bind:value={method} onchange={() => list.set('payment_method', method)} options={PAYMENT_METHODS} placeholder="Any method" aria-label="Payment method" />
		<Select class="w-36" bind:value={status} onchange={() => list.set('status', status)} options={[{ value: 'ACTIVE', label: 'Active' }, { value: 'CANCELLED', label: 'Cancelled' }]} placeholder="Any status" aria-label="Status" />
		<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-9 w-40" onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-9 w-40" onchange={() => list.set('end_date', end)} /></label>
	</div>
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="expenses-table">
			<thead><tr><th>Number</th><th>Date</th><th>Category</th><th>Description</th><th>Paid to</th><th>Method</th><th class="num">Amount</th><th class="num">Tax</th><th class="num">Total</th><th>Status</th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={10} onRetry={() => list.load()} />
				{#each list.items as e (e.id)}
					<tr class:opacity-60={e.status === 'CANCELLED'}>
						<td><a class="font-medium text-primary hover:underline" href="/expenses/{e.id}">{e.expense_number}</a></td>
						<td class="whitespace-nowrap">{fmtDate(e.date)}</td>
						<td>{e.category_name}</td>
						<td>{e.description}</td>
						<td class="text-muted-foreground">{e.paid_to}</td>
						<td>{e.payment_method_display}</td>
						<td class="num">{money(e.amount)}</td>
						<td class="num">{money(e.tax_amount, true)}</td>
						<td class="num font-medium">{money(e.total_amount)}</td>
						<td><StatusBadge status={e.status} /></td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
