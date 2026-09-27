<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { api } from '$lib/api/client';
	import { money, date as fmtDate, today } from '$lib/utilities/format';
	import type { AgingBucket, PartyType } from '$lib/types';
	import { SIDES } from '$lib/components/parties/config';
	import Input from '$lib/components/ui/input.svelte';

	interface Detail {
		buckets: AgingBucket[];
		totals: Record<string, string>;
		documents: { id: number; number: string; date: string; due_date: string; total_amount: string; paid_as_of: string; outstanding: string; days_overdue: number; bucket: string }[];
		unallocated_payments: { id: number; number: string; date: string; amount: string; unallocated: string }[];
	}

	let { type, partyId }: { type: PartyType; partyId: number } = $props();
	const side = $derived(SIDES[type]);
	let asOf = $state(today());
	const q = createQuery(() => ({
		queryKey: ['party-aging', type, partyId, asOf],
		queryFn: () => api.get<Detail>(`${side.api}/${partyId}/aging/`, { as_of: asOf })
	}));
	const d = $derived(q.data);
</script>

<div class="flex items-end gap-2 border-b p-3">
	<label class="grid gap-1 text-xs text-muted-foreground">As of<Input type="date" bind:value={asOf} class="h-8 w-40" /></label>
</div>
{#if d}
	<div class="grid grid-cols-2 gap-2 border-b p-3 sm:grid-cols-4 lg:grid-cols-8">
		{#each d.buckets as b (b.key)}
			<div class="rounded-md border p-2"><p class="text-[11px] text-muted-foreground">{b.label}</p><p class="font-semibold tabular-nums">{money(d.totals[b.key])}</p></div>
		{/each}
		<div class="rounded-md border bg-muted/40 p-2"><p class="text-[11px] text-muted-foreground">Total</p><p class="font-semibold tabular-nums">{money(d.totals.total)}</p></div>
		<div class="rounded-md border p-2"><p class="text-[11px] text-muted-foreground">Unallocated / advance</p><p class="font-semibold tabular-nums">{money(d.totals.unallocated)}</p></div>
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>{side.docLabel}</th><th>Date</th><th>Due date</th><th class="num">Total</th><th class="num">Paid (as of)</th><th class="num">Outstanding</th><th class="num">Days overdue</th><th>Bucket</th></tr></thead>
			<tbody>
				{#each d.documents as doc (doc.id)}
					<tr>
						<td><a class="text-primary hover:underline" href="{side.docRoute}/{doc.id}">{doc.number}</a></td>
						<td>{fmtDate(doc.date)}</td><td>{fmtDate(doc.due_date)}</td>
						<td class="num">{money(doc.total_amount)}</td><td class="num">{money(doc.paid_as_of)}</td>
						<td class="num font-medium">{money(doc.outstanding)}</td>
						<td class="num" class:text-red-600={doc.days_overdue > 0}>{doc.days_overdue}</td>
						<td class="text-xs">{d.buckets.find((b) => b.key === doc.bucket)?.label}</td>
					</tr>
				{:else}
					<tr><td colspan="8" class="py-6 text-center text-muted-foreground">Nothing outstanding as of {fmtDate(asOf)}.</td></tr>
				{/each}
			</tbody>
		</table>
	</div>
	{#if d.unallocated_payments.length}
		<p class="border-t px-3 pt-3 text-sm font-medium">Unallocated {side.payLabel.toLowerCase()}s (advances — not aged)</p>
		<div class="overflow-x-auto">
			<table class="table-base">
				<thead><tr><th>{side.payLabel}</th><th>Date</th><th class="num">Amount</th><th class="num">Unallocated</th></tr></thead>
				<tbody>
					{#each d.unallocated_payments as p (p.id)}
						<tr><td><a class="text-primary hover:underline" href="{side.payRoute}/{p.id}">{p.number}</a></td><td>{fmtDate(p.date)}</td><td class="num">{money(p.amount)}</td><td class="num">{money(p.unallocated)}</td></tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
{/if}
