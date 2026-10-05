<script lang="ts">
	import { page } from '$app/state';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money, date as fmtDate, PAYMENT_METHODS } from '$lib/utilities/format';
	import type { ExpenseCategory, Paginated } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';

	interface Row {
		id: number;
		date: string;
		number: string;
		category_id: number;
		category_name: string;
		description: string;
		paid_to: string;
		payment_method: string;
		amount: string;
		tax_amount: string;
		total_amount: string;
		status: string;
	}
	interface Summary {
		total_expenses: number;
		amount: string;
		tax_amount: string;
		total_amount: string;
		by_category: { category_id: number; category_name: string; count: number; amount: string; tax_amount: string; total_amount: string }[];
	}

	const path = 'reports/expenses/';
	const initialCategory = page.url.searchParams.get('category') ?? '';
	let category = $state(initialCategory);
	const list = new ListState<Row, Summary>(path, { category: initialCategory });
	list.load();
	let categories = $state<{ value: string; label: string }[]>([]);
	api.get<Paginated<ExpenseCategory>>('expense-categories/', { page_size: 100 }).then((d) => {
		categories = d.results.map((c) => ({ value: String(c.id), label: c.name }));
	});

	let start = $state('');
	let end = $state('');
	let method = $state('');
	let status = $state('ACTIVE');
	const s = $derived(list.summary);
	const share = (v: string) => (s && Number(s.total_amount) > 0 ? ((Number(v) / Number(s.total_amount)) * 100).toFixed(1) : '0.0');
</script>

<svelte:head><title>Expense Report · Accounting</title></svelte:head>

<PageHeader title="Expense Report" description="Expenses by date and category. Cancelled expenses are excluded unless selected.">
	{#snippet actions()}<ExportButtons {path} query={list.filters} />{/snippet}
</PageHeader>

<Card class="no-print mb-4">
	<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
		<label class="grid gap-1 text-xs text-muted-foreground">Start date<Input type="date" bind:value={start} onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">End date<Input type="date" bind:value={end} onchange={() => list.set('end_date', end)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">Category<Select bind:value={category} onchange={() => list.set('category', category)} options={categories} placeholder="All categories" /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">Method<Select bind:value={method} onchange={() => list.set('payment_method', method)} options={PAYMENT_METHODS} placeholder="Any" /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">Status<Select bind:value={status} onchange={() => list.set('status', status)} options={[{ value: 'ACTIVE', label: 'Active' }, { value: 'CANCELLED', label: 'Cancelled' }, { value: 'ALL', label: 'All' }]} /></label>
	</div>
</Card>

{#if s}
	<div class="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4" data-testid="report-summary">
		{#each [['Expenses', String(s.total_expenses)], ['Amount excl. tax', money(s.amount)], [auth.taxLabel, money(s.tax_amount)], [`Total (${auth.currency})`, money(s.total_amount)]] as [label, value] (label)}
			<div class="rounded-xl border bg-card p-3 shadow-xs"><p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</p><p class="mt-1 font-semibold tabular-nums">{value}</p></div>
		{/each}
	</div>

	<Card title="By category" class="mb-4" bodyClass="p-0">
		<div class="overflow-x-auto">
			<table class="table-base" data-testid="category-breakdown">
				<thead><tr><th>Category</th><th class="num">Expenses</th><th class="num">Amount</th><th class="num">Tax</th><th class="num">Total</th><th class="w-1/4">Share</th></tr></thead>
				<tbody>
					{#each s.by_category as c (c.category_id)}
						<tr>
							<td class="font-medium">{c.category_name}</td>
							<td class="num">{c.count}</td>
							<td class="num">{money(c.amount)}</td>
							<td class="num">{money(c.tax_amount, true)}</td>
							<td class="num font-medium">{money(c.total_amount)}</td>
							<td>
								<div class="flex items-center gap-2">
									<div class="h-2 flex-1 rounded-full bg-muted"><div class="h-2 rounded-full bg-primary" style="width: {share(c.total_amount)}%"></div></div>
									<span class="w-12 text-right text-xs tabular-nums text-muted-foreground">{share(c.total_amount)}%</span>
								</div>
							</td>
						</tr>
					{:else}
						<tr><td colspan="6" class="py-6 text-center text-muted-foreground">No expenses for these filters.</td></tr>
					{/each}
				</tbody>
			</table>
		</div>
	</Card>
{/if}

<Card bodyClass="p-0">
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="report-table">
			<thead><tr><th>Date</th><th>Expense #</th><th>Category</th><th>Description</th><th>Paid to</th><th>Method</th><th class="num">Amount</th><th class="num">Tax</th><th class="num">Total</th><th>Status</th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={10} />
				{#each list.items as r (r.id)}
					<tr class:opacity-60={r.status === 'CANCELLED'}>
						<td class="whitespace-nowrap">{fmtDate(r.date)}</td>
						<td><a class="text-primary hover:underline" href="/expenses/{r.id}">{r.number}</a></td>
						<td>{r.category_name}</td>
						<td>{r.description}</td>
						<td class="text-muted-foreground">{r.paid_to}</td>
						<td>{r.payment_method}</td>
						<td class="num">{money(r.amount)}</td>
						<td class="num">{money(r.tax_amount, true)}</td>
						<td class="num font-medium">{money(r.total_amount)}</td>
						<td><StatusBadge status={r.status} /></td>
					</tr>
				{/each}
			</tbody>
			{#if s && list.items.length}
				<tfoot><tr><td colspan="6">TOTAL ({s.total_expenses})</td><td class="num">{money(s.amount)}</td><td class="num">{money(s.tax_amount)}</td><td class="num">{money(s.total_amount)}</td><td></td></tr></tfoot>
			{/if}
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
