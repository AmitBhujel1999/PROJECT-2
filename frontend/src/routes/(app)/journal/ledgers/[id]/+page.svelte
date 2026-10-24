<script lang="ts">
	import { page } from '$app/state';
	import { api, ApiError } from '$lib/api/client';
	import { money, date as fmtDate } from '$lib/utilities/format';
	import type { DrCr, LedgerBook } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';

	const id = $derived(Number(page.params.id));
	let start = $state('');
	let end = $state('');
	let book = $state<LedgerBook | null>(null);
	let loading = $state(false);
	let error = $state<string | null>(null);
	const query = $derived({ start_date: start || undefined, end_date: end || undefined });
	const path = $derived(`journal/ledgers/${id}/book/`);

	$effect(() => {
		load(path, query);
	});

	async function load(p: string, q: Record<string, string | undefined>) {
		loading = true;
		error = null;
		try {
			book = await api.get<LedgerBook>(p, q);
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not load the ledger.';
		} finally {
			loading = false;
		}
	}

	const show = (b: DrCr | undefined) => (b ? `${money(b.amount)}${Number(b.amount) ? ` ${b.side}` : ''}` : '');
</script>

<svelte:head><title>{book?.ledger_name ?? 'Ledger'} · Mero Lekhapal</title></svelte:head>

<PageHeader title={book?.ledger_name ?? 'Ledger'} description={book?.group_display} back={{ href: '/journal/ledgers', label: 'Chart of Accounts' }} />

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end justify-between gap-2 border-b p-3 no-print">
		<div class="flex flex-wrap items-end gap-2">
			<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-8 w-40" /></label>
			<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-8 w-40" /></label>
		</div>
		<div class="flex gap-2"><ExportButtons {path} {query} /></div>
	</div>
	<div class="grid grid-cols-2 gap-3 border-b bg-muted/30 p-3 text-sm sm:grid-cols-4">
		<div><p class="text-xs text-muted-foreground">Opening balance</p><p class="font-semibold tabular-nums">{show(book?.opening)}</p></div>
		<div><p class="text-xs text-muted-foreground">Total debit</p><p class="font-semibold tabular-nums">{money(book?.total_debit)}</p></div>
		<div><p class="text-xs text-muted-foreground">Total credit</p><p class="font-semibold tabular-nums">{money(book?.total_credit)}</p></div>
		<div><p class="text-xs text-muted-foreground">Closing balance</p><p class="font-semibold tabular-nums" data-testid="ledger-closing">{show(book?.closing)}</p></div>
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>Date</th><th>Voucher</th><th>Particulars</th><th class="num">Debit</th><th class="num">Credit</th><th class="num">Balance</th></tr></thead>
			<tbody>
				<TableState {loading} {error} empty={false} colspan={6} onRetry={() => load(path, query)} />
				{#if book && !loading}
					<tr class="bg-muted/20"><td>{fmtDate(book.start_date)}</td><td colspan="4" class="font-medium">Opening balance</td><td class="num font-medium">{show(book.opening)}</td></tr>
					{#each book.entries as e, i (i)}
						<tr>
							<td class="whitespace-nowrap">{fmtDate(e.date)}</td>
							<td class="whitespace-nowrap"><a class="text-primary hover:underline" href="/journal/{e.voucher_id}">{e.number}</a></td>
							<td class="min-w-48">{e.narration}</td>
							<td class="num">{money(e.debit, true)}</td>
							<td class="num">{money(e.credit, true)}</td>
							<td class="num font-medium">{show(e.balance)}</td>
						</tr>
					{:else}
						<tr><td colspan="6" class="py-6 text-center text-muted-foreground">No entries in this period.</td></tr>
					{/each}
					<tr class="bg-muted/40 font-semibold"><td></td><td colspan="2">Closing balance</td><td class="num">{money(book.total_debit)}</td><td class="num">{money(book.total_credit)}</td><td class="num">{show(book.closing)}</td></tr>
				{/if}
			</tbody>
		</table>
	</div>
</Card>
