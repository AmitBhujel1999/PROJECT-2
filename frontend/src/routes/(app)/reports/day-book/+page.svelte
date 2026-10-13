<script lang="ts">
	import { api, ApiError } from '$lib/api/client';
	import { money, date as fmtDate, today, addDays } from '$lib/utilities/format';
	import type { DayBook } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';

	let start = $state(today());
	let end = $state(today());
	let showCancelled = $state(false);
	let data = $state<DayBook | null>(null);
	let loading = $state(false);
	let error = $state<string | null>(null);

	const query = $derived({ start_date: start, end_date: end || start, status: showCancelled ? 'ALL' : undefined });

	$effect(() => {
		load(query);
	});

	async function load(q: Record<string, string | undefined>) {
		loading = true;
		error = null;
		try {
			data = await api.get<DayBook>('reports/day-book/', q);
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not load the day book.';
		} finally {
			loading = false;
		}
	}

	function shift(days: number) {
		start = addDays(start, days);
		end = start;
	}
</script>

<svelte:head><title>Day Book · Accounting</title></svelte:head>

<PageHeader title="Day Book" description="Every entry made on a day: sales, purchases, receipts, payments, expenses and contra." />

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end justify-between gap-2 border-b p-3 no-print">
		<div class="flex flex-wrap items-end gap-2">
			<Button size="sm" variant="outline" onclick={() => shift(-1)} aria-label="Previous day">‹ Prev</Button>
			<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-8 w-40" data-testid="daybook-start" /></label>
			<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-8 w-40" /></label>
			<Button size="sm" variant="outline" onclick={() => shift(1)} aria-label="Next day">Next ›</Button>
			<Button size="sm" variant="ghost" onclick={() => ((start = today()), (end = today()))}>Today</Button>
			<label class="flex h-8 items-center gap-2 text-sm"><input type="checkbox" bind:checked={showCancelled} /> Show cancelled</label>
		</div>
		<div class="flex gap-2"><ExportButtons path="reports/day-book/" {query} /></div>
	</div>
	{#if data?.totals.length}
		<div class="flex flex-wrap gap-x-6 gap-y-2 border-b bg-muted/30 p-3 text-sm">
			{#each data.totals as t (t.voucher_type)}
				<div><p class="text-xs text-muted-foreground">{t.voucher_type}</p><p class="font-semibold tabular-nums">{money(t.amount)}</p></div>
			{/each}
		</div>
	{/if}
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="daybook-table">
			<thead><tr><th>Date</th><th>Type</th><th>Voucher</th><th>Particulars</th><th>Cash / Bank</th><th class="num">Amount</th><th>Status</th></tr></thead>
			<tbody>
				<TableState {loading} {error} empty={!!data && !data.entries.length} colspan={7} onRetry={() => load(query)} emptyText="No entries on this day." />
				{#if data && !loading}
					{#each data.entries as e (e.link + e.object_id)}
						<tr class:opacity-60={e.status === 'CANCELLED'}>
							<td class="whitespace-nowrap">{fmtDate(e.date)}</td>
							<td>{e.voucher_type}</td>
							<td class="whitespace-nowrap"><a class="text-primary hover:underline" href="/{e.link}/{e.object_id}">{e.number}</a></td>
							<td class="min-w-48">{e.particulars}</td>
							<td class="text-muted-foreground">{e.account_name}</td>
							<td class="num font-medium">{money(e.amount)}</td>
							<td><StatusBadge status={e.status} /></td>
						</tr>
					{/each}
				{/if}
			</tbody>
		</table>
	</div>
</Card>
