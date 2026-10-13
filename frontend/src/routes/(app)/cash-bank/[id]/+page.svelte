<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { api, ApiError } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { money, date as fmtDate, firstOfMonth } from '$lib/utilities/format';
	import type { AccountBook, AccountBookEntry } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import AccountSelect from '$lib/components/cashbank/account-select.svelte';

	const id = $derived(Number(page.params.id));
	let start = $state(firstOfMonth());
	let end = $state('');
	let book = $state<AccountBook | null>(null);
	let loading = $state(false);
	let error = $state<string | null>(null);
	let picked = $state<string | number | null>(null);

	const query = $derived({ start_date: start || undefined, end_date: end || undefined });
	const path = $derived(`cash-bank/accounts/${id}/book/`);

	$effect(() => {
		picked = id;
		load(path, query);
	});

	async function load(p: string, q: Record<string, string | undefined>) {
		loading = true;
		error = null;
		try {
			book = await api.get<AccountBook>(p, q);
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not load the book.';
		} finally {
			loading = false;
		}
	}

	const ROUTES: Record<AccountBookEntry['voucher_type'], string> = {
		Receipt: '/receipts',
		Payment: '/payments',
		Expense: '/expenses',
		Contra: '/cash-bank/contra',
		Journal: '/journal'
	};
</script>

<svelte:head><title>{book?.account_name ?? 'Cash & Bank'} book · Accounting</title></svelte:head>

<PageHeader title="{book?.account_name ?? ''} book" description="Money in and out of this account with a running balance." back={{ href: '/cash-bank', label: 'Cash & Bank' }} />

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end justify-between gap-2 border-b p-3 no-print">
		<div class="flex flex-wrap items-end gap-2">
			<label class="grid gap-1 text-xs text-muted-foreground">Account
				<AccountSelect class="h-8 w-52" bind:value={picked} placeholder="" onchange={() => picked && goto(`/cash-bank/${picked}`)} />
			</label>
			<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-8 w-40" /></label>
			<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-8 w-40" /></label>
		</div>
		<div class="flex gap-2"><ExportButtons {path} {query} /></div>
	</div>
	<div class="grid grid-cols-2 gap-3 border-b bg-muted/30 p-3 text-sm sm:grid-cols-4">
		<div><p class="text-xs text-muted-foreground">Opening balance</p><p class="font-semibold tabular-nums">{money(book?.opening_balance)}</p></div>
		<div><p class="text-xs text-muted-foreground">Money in</p><p class="font-semibold tabular-nums text-emerald-700">{money(book?.total_in)}</p></div>
		<div><p class="text-xs text-muted-foreground">Money out</p><p class="font-semibold tabular-nums text-red-700">{money(book?.total_out)}</p></div>
		<div><p class="text-xs text-muted-foreground">Closing balance ({auth.currency})</p><p class="font-semibold tabular-nums" data-testid="book-closing">{money(book?.closing_balance)}</p></div>
	</div>
	<div class="overflow-x-auto">
		<table class="table-base" data-testid="book-table">
			<thead>
				<tr><th>Date</th><th>Type</th><th>Voucher</th><th>Particulars</th><th>Ref.</th><th class="num">In</th><th class="num">Out</th><th class="num">Balance</th></tr>
			</thead>
			<tbody>
				<TableState {loading} {error} empty={false} colspan={8} onRetry={() => load(path, query)} />
				{#if book && !loading}
					<tr class="bg-muted/20">
						<td class="whitespace-nowrap">{fmtDate(book.start_date)}</td><td colspan="6" class="font-medium">Opening balance</td>
						<td class="num font-medium">{money(book.opening_balance)}</td>
					</tr>
					{#each book.entries as e (e.voucher_type + e.object_id + e.money_in)}
						<tr>
							<td class="whitespace-nowrap">{fmtDate(e.date)}</td>
							<td>{e.voucher_type}</td>
							<td class="whitespace-nowrap"><a class="text-primary hover:underline" href="{ROUTES[e.voucher_type]}/{e.object_id}">{e.number}</a></td>
							<td class="min-w-48">{e.particulars}</td>
							<td class="text-muted-foreground">{e.reference_number}</td>
							<td class="num text-emerald-700">{money(e.money_in, true)}</td>
							<td class="num text-red-700">{money(e.money_out, true)}</td>
							<td class="num font-medium">{money(e.balance)}</td>
						</tr>
					{:else}
						<tr><td colspan="8" class="py-6 text-center text-muted-foreground">No entries in this period.</td></tr>
					{/each}
					<tr class="bg-muted/40 font-semibold">
						<td></td><td colspan="4">Closing balance</td>
						<td class="num">{money(book.total_in)}</td><td class="num">{money(book.total_out)}</td><td class="num">{money(book.closing_balance)}</td>
					</tr>
				{/if}
			</tbody>
		</table>
	</div>
</Card>
