<script lang="ts">
	import { api, ApiError } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { money, today } from '$lib/utilities/format';
	import { fiscalYearStart } from '$lib/utilities/nepali-date';
	import type { CashFlow, StatementRow } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import StatementTable, { type Line } from '$lib/components/statements/statement-table.svelte';
	import { rowHref } from '$lib/components/statements/links';

	let start = $state(fiscalYearStart(today()));
	let end = $state(today());
	let d = $state<CashFlow | null>(null);
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
			d = await api.get<CashFlow>('reports/cash-flow/', q);
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not load the cash flow.';
		} finally {
			loading = false;
		}
	}

	const items = (rows: StatementRow[]): Line[] => rows.map((r) => ({ label: r.name, amount: r.amount, indent: true, href: rowHref(r) }));

	const lines = $derived.by((): Line[] => {
		if (!d) return [];
		const section = (title: string, rows: StatementRow[], net: string): Line[] => [
			{ label: title, strong: true },
			...(rows.length ? items(rows) : [{ label: 'None', indent: true }]),
			{ label: `Net cash from ${title.toLowerCase()}`, total: net }
		];
		return [
			{ label: 'Opening cash & bank balance', total: d.opening_cash, strong: true },
			...section('Operating activities', d.operating, d.net_operating),
			...section('Investing activities', d.investing, d.net_investing),
			...section('Financing activities', d.financing, d.net_financing),
			{ label: 'Net increase / (decrease) in cash', total: d.net_change, strong: true, grand: true },
			{ label: 'Closing cash & bank balance', total: d.closing_cash, strong: true, grand: true },
			...d.accounts.filter((a) => Number(a.amount)).map((a) => ({ label: a.name, amount: a.amount, indent: true, href: `/cash-bank/${a.id}` }))
		];
	});
</script>

<svelte:head><title>Cash Flow · Accounting</title></svelte:head>

<PageHeader title="Cash Flow" description="Where cash and bank money came from and went, from the cash/bank books. Transfers between your own accounts are left out." />

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end justify-between gap-2 border-b p-3 no-print">
		<div class="flex flex-wrap items-end gap-2">
			<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-8 w-40" required /></label>
			<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-8 w-40" /></label>
		</div>
		<div class="flex gap-2"><ExportButtons path="reports/cash-flow/" {query} /></div>
	</div>
	{#if d}
		<div class="grid grid-cols-3 gap-3 border-b bg-muted/30 p-3 text-sm">
			<div><p class="text-xs text-muted-foreground">Opening</p><p class="font-semibold tabular-nums">{money(d.opening_cash)}</p></div>
			<div><p class="text-xs text-muted-foreground">Net change</p><p class="font-semibold tabular-nums" class:text-destructive={d.net_change.startsWith('-')}>{money(d.net_change)}</p></div>
			<div><p class="text-xs text-muted-foreground">Closing ({auth.currency})</p><p class="font-semibold tabular-nums" data-testid="cf-closing">{money(d.closing_cash)}</p></div>
		</div>
	{/if}
	{#if error}<p class="p-4 text-sm text-destructive">{error}</p>{/if}
	<div class="overflow-x-auto" class:opacity-60={loading}><StatementTable {lines} testid="cf-table" /></div>
</Card>
