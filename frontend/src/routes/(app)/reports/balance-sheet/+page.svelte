<script lang="ts">
	import { CircleAlert } from '@lucide/svelte';
	import { api, ApiError } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { money, today } from '$lib/utilities/format';
	import type { BalanceSheet, StatementGroup } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import StatementTable, { type Line } from '$lib/components/statements/statement-table.svelte';
	import { rowHref } from '$lib/components/statements/links';

	let asOf = $state(today());
	let d = $state<BalanceSheet | null>(null);
	let error = $state<string | null>(null);
	let loading = $state(false);
	const query = $derived({ as_of: asOf || undefined });

	$effect(() => {
		load(query);
	});

	async function load(q: Record<string, string | undefined>) {
		loading = true;
		error = null;
		try {
			d = await api.get<BalanceSheet>('reports/balance-sheet/', q);
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not load the balance sheet.';
		} finally {
			loading = false;
		}
	}

	function side(groups: StatementGroup[], total: string, label: string): Line[] {
		return [
			...groups.flatMap((g) => [
				{ label: g.label, total: g.total, strong: true },
				...g.rows.map((r) => ({ label: r.name, amount: r.amount, indent: true, href: rowHref(r, g.key === 'CASH_BANK' ? 'cash' : 'ledger') }))
			]),
			{ label, total, strong: true, grand: true }
		];
	}
</script>

<svelte:head><title>Balance Sheet · Accounting</title></svelte:head>

<PageHeader title="Balance Sheet" description="What the business owns and owes on a date. Stock at weighted average cost." />

<Card bodyClass="p-0" class="mb-4">
	<div class="flex flex-wrap items-end justify-between gap-2 p-3 no-print">
		<label class="grid gap-1 text-xs text-muted-foreground">As at<Input type="date" bind:value={asOf} class="h-8 w-40" /></label>
		<div class="flex gap-2"><ExportButtons path="reports/balance-sheet/" {query} /></div>
	</div>
</Card>

{#if error}<p class="mb-4 text-sm text-destructive">{error}</p>{/if}
{#if d && Number(d.difference) !== 0}
	<p class="mb-4 flex items-center gap-2 rounded-md border border-amber-300 bg-amber-50 px-4 py-2 text-sm text-amber-900">
		<CircleAlert class="size-4" />The two sides differ by {money(d.difference)}. Please report this; it should never happen.
	</p>
{/if}

{#if d}
	<div class="grid gap-4 lg:grid-cols-2" class:opacity-60={loading}>
		<Card title="Capital & Liabilities ({auth.currency})" bodyClass="p-0">
			<div class="overflow-x-auto"><StatementTable lines={side(d.liabilities, d.total_liabilities, 'Total')} testid="bs-liabilities" /></div>
		</Card>
		<Card title="Assets ({auth.currency})" bodyClass="p-0">
			<div class="overflow-x-auto"><StatementTable lines={side(d.assets, d.total_assets, 'Total')} testid="bs-assets" /></div>
		</Card>
	</div>
	<p class="mt-3 text-xs text-muted-foreground">
		"Difference in opening balances" is the total of the opening cash/bank, opening stock and ledger opening balances, which have no matching entry. Enter your capital as a ledger opening balance to reduce it.
	</p>
{/if}
