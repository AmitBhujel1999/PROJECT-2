<script lang="ts">
	import { api, ApiError } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { money, today } from '$lib/utilities/format';
	import { fiscalYearStart } from '$lib/utilities/nepali-date';
	import type { ProfitLoss, StatementRow } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import StatementTable, { type Line } from '$lib/components/statements/statement-table.svelte';
	import { rowHref } from '$lib/components/statements/links';

	let start = $state(fiscalYearStart(today()));
	let end = $state(today());
	let d = $state<ProfitLoss | null>(null);
	let error = $state<string | null>(null);
	let loading = $state(false);
	const query = $derived({ start_date: start || undefined, end_date: end || undefined });

	$effect(() => {
		load(query);
	});

	async function load(q: Record<string, string | undefined>) {
		loading = true;
		error = null;
		try {
			d = await api.get<ProfitLoss>('reports/profit-loss/', q);
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not load the statement.';
		} finally {
			loading = false;
		}
	}

	const neg = (v: string) => (Number(v) === 0 ? v : v.startsWith('-') ? v.slice(1) : `-${v}`);
	const items = (rows: StatementRow[], kind: 'ledger' | 'category', sign = 1): Line[] =>
		rows.map((r) => ({ label: r.name, amount: sign < 0 ? neg(r.amount) : r.amount, indent: true, href: rowHref(r, kind) }));

	const lines = $derived.by((): Line[] => {
		if (!d) return [];
		return [
			{ label: 'Sales (excluding VAT)', total: d.sales, href: '/reports/sales' },
			...(d.direct_income.length ? [{ label: 'Direct income', strong: true }, ...items(d.direct_income, 'ledger')] : []),
			{ label: 'Less: Cost of goods sold', strong: true },
			{ label: 'Opening stock', amount: d.opening_stock, indent: true },
			...(Number(d.opening_stock_added) ? [{ label: 'Opening stock added', amount: d.opening_stock_added, indent: true }] : []),
			{ label: 'Purchases (excluding VAT)', amount: d.purchases, indent: true, href: '/reports/purchases' },
			{ label: 'Less: Closing stock', amount: neg(d.closing_stock), indent: true, href: '/reports/stock' },
			{ label: 'Cost of goods sold', total: neg(d.cost_of_goods_sold) },
			...(d.direct_expenses.length ? [{ label: 'Direct expenses', strong: true }, ...items(d.direct_expenses, 'ledger', -1)] : []),
			{ label: Number(d.gross_profit) >= 0 ? 'Gross profit' : 'Gross loss', total: d.gross_profit, strong: true, grand: true },
			...(d.indirect_income.length ? [{ label: 'Add: Indirect income', strong: true }, ...items(d.indirect_income, 'ledger')] : []),
			{ label: 'Less: Expenses', strong: true },
			...items(d.expense_categories, 'category', -1),
			...items(d.indirect_expenses, 'ledger', -1),
			{ label: 'Total expenses', total: neg(d.total_indirect_expenses) },
			{ label: Number(d.net_profit) >= 0 ? 'NET PROFIT' : 'NET LOSS', total: d.net_profit, strong: true, grand: true }
		];
	});
</script>

<svelte:head><title>Profit & Loss · Accounting</title></svelte:head>

<PageHeader title="Profit & Loss" description="Income minus cost of goods sold and expenses. Amounts exclude VAT; stock at weighted average cost." />

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end justify-between gap-2 border-b p-3 no-print">
		<div class="flex flex-wrap items-end gap-2">
			<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-8 w-40" /></label>
			<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-8 w-40" /></label>
		</div>
		<div class="flex gap-2"><ExportButtons path="reports/profit-loss/" {query} /></div>
	</div>
	{#if d}
		<div class="grid grid-cols-2 gap-3 border-b bg-muted/30 p-3 text-sm sm:grid-cols-3">
			<div><p class="text-xs text-muted-foreground">Sales</p><p class="font-semibold tabular-nums">{money(d.sales)}</p></div>
			<div><p class="text-xs text-muted-foreground">Gross profit</p><p class="font-semibold tabular-nums">{money(d.gross_profit)}</p></div>
			<div><p class="text-xs text-muted-foreground">Net profit ({auth.currency})</p>
				<p class="font-semibold tabular-nums" class:text-destructive={d.net_profit.startsWith('-')} data-testid="net-profit">{money(d.net_profit)}</p></div>
		</div>
	{/if}
	{#if error}<p class="p-4 text-sm text-destructive">{error}</p>{:else if loading && !d}<p class="p-4 text-sm text-muted-foreground">Loading…</p>{/if}
	<div class="overflow-x-auto" class:opacity-60={loading}><StatementTable {lines} testid="pl-table" /></div>
</Card>
