<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { money, date as fmtDate, firstOfMonth, today } from '$lib/utilities/format';
	import type { LedgerEntry, PartyHeader, PartyType } from '$lib/types';
	import { SIDES } from '$lib/components/parties/config';
	import Input from '$lib/components/ui/input.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import Spinner from '$lib/components/ui/spinner.svelte';

	interface Statement {
		party: PartyHeader;
		opening_balance: string;
		closing_balance: string;
		total_debit: string;
		total_credit: string;
		start_date: string;
		end_date: string;
		entries: LedgerEntry[];
	}

	let { type, partyId }: { type: PartyType; partyId: number } = $props();
	const side = $derived(SIDES[type]);
	let start = $state(firstOfMonth());
	let end = $state(today());

	const q = createQuery(() => ({
		queryKey: ['statement', type, partyId, start, end],
		queryFn: () => api.get<Statement>(`${side.api}/${partyId}/statement/`, { start_date: start, end_date: end })
	}));
	const s = $derived(q.data);
</script>

<div class="no-print flex flex-wrap items-end justify-between gap-2 border-b p-3">
	<div class="flex flex-wrap items-end gap-2">
		<label class="grid gap-1 text-xs text-muted-foreground">Period from<Input type="date" bind:value={start} class="h-8 w-40" /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-8 w-40" /></label>
	</div>
	<div class="flex gap-2"><ExportButtons path="{side.api}/{partyId}/statement/" query={{ start_date: start, end_date: end }} /></div>
</div>

{#if q.isPending}
	<p class="flex items-center gap-2 p-6 text-muted-foreground"><Spinner /> Loading statement…</p>
{:else if s}
	<div class="p-4 sm:p-6" data-testid="statement">
		<div class="mb-4 flex flex-wrap justify-between gap-4">
			<div>
				<p class="text-xs uppercase tracking-wide text-muted-foreground">{auth.settings?.business_name}</p>
				<h3 class="text-lg font-semibold uppercase">{s.party.name}</h3>
				{#if s.party.pan_vat_no}<p class="text-sm text-muted-foreground">PAN/VAT: {s.party.pan_vat_no}</p>{/if}
				{#if s.party.address}<p class="text-sm text-muted-foreground">{s.party.address}</p>{/if}
			</div>
			<div class="text-right text-sm">
				<p class="font-semibold">{side.label} Statement</p>
				<p>Period: {fmtDate(s.start_date)} – {fmtDate(s.end_date)}</p>
				<p class="mt-1">Opening balance: <b class="tabular-nums">{money(s.opening_balance)}</b></p>
			</div>
		</div>
		<div class="overflow-x-auto">
			<table class="table-base">
				<thead><tr><th>Date</th><th>Ref</th><th>Description</th><th class="num">Debit</th><th class="num">Credit</th><th class="num">Balance</th></tr></thead>
				<tbody>
					<tr class="bg-muted/30"><td>{fmtDate(s.start_date)}</td><td></td><td class="italic">Opening balance</td><td></td><td></td><td class="num">{money(s.opening_balance)}</td></tr>
					{#each s.entries as e, i (i)}
						<tr>
							<td class="whitespace-nowrap">{fmtDate(e.date)}</td>
							<td class="whitespace-nowrap">{e.reference}</td>
							<td>{e.description}</td>
							<td class="num">{money(e.debit, true)}</td>
							<td class="num">{money(e.credit, true)}</td>
							<td class="num">{money(e.balance)}</td>
						</tr>
					{:else}
						<tr><td colspan="6" class="py-6 text-center text-muted-foreground">No transactions in this period.</td></tr>
					{/each}
				</tbody>
				<tfoot>
					<tr><td colspan="3">Closing balance</td><td class="num">{money(s.total_debit)}</td><td class="num">{money(s.total_credit)}</td><td class="num" data-testid="statement-closing">{money(s.closing_balance)}</td></tr>
				</tfoot>
			</table>
		</div>
	</div>
{/if}
