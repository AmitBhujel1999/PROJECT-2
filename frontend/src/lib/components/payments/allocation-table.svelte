<script lang="ts">
	import { money, date as fmtDate, toCents, fromCents } from '$lib/utilities/format';
	import type { OpenDocument } from '$lib/types';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';

	let {
		docs,
		amounts = $bindable({}),
		available,
		docLabel,
		docRoute
	}: { docs: OpenDocument[]; amounts?: Record<number, string>; available: string; docLabel: string; docRoute: string } = $props();

	const allocated = $derived(Object.values(amounts).reduce((sum, v) => sum + toCents(v), 0n));
	const remaining = $derived(toCents(available) - allocated);

	function fill(doc: OpenDocument) {
		const rem = remaining + toCents(amounts[doc.id]);
		const bal = toCents(doc.balance_due);
		const v = rem < bal ? rem : bal;
		amounts[doc.id] = v > 0n ? fromCents(v) : '';
	}
</script>

<div class="overflow-x-auto">
	<table class="table-base" data-testid="allocation-table">
		<thead><tr><th>{docLabel}</th><th>Date</th><th>Due</th><th class="num">Overdue</th><th class="num">Total</th><th class="num">Balance</th><th>Status</th><th class="num w-40">Allocate</th></tr></thead>
		<tbody>
			{#each docs as d (d.id)}
				<tr>
					<td><a class="text-primary hover:underline" href="{docRoute}/{d.id}" target="_blank">{d.number}</a></td>
					<td class="whitespace-nowrap">{fmtDate(d.date)}</td>
					<td class="whitespace-nowrap">{fmtDate(d.due_date)}</td>
					<td class="num" class:text-red-600={d.days_overdue > 0}>{d.days_overdue ? `${d.days_overdue}d` : '—'}</td>
					<td class="num">{money(d.total_amount)}</td>
					<td class="num font-medium">{money(d.balance_due)}</td>
					<td><StatusBadge status={d.payment_status} /></td>
					<td>
						<div class="flex items-center gap-1">
							<input type="number" min="0" step="0.01" bind:value={amounts[d.id]} aria-label="Allocate to {d.number}"
								class="h-8 w-full rounded-md border bg-background px-2 text-right text-sm tabular-nums" />
							<button type="button" class="rounded border px-1.5 py-1 text-xs hover:bg-muted" onclick={() => fill(d)} title="Fill balance">Max</button>
						</div>
					</td>
				</tr>
			{:else}
				<tr><td colspan="8" class="py-6 text-center text-muted-foreground">No open {docLabel.toLowerCase()}s.</td></tr>
			{/each}
		</tbody>
	</table>
</div>
<div class="flex flex-wrap justify-end gap-4 border-t px-3 py-2 text-sm">
	<span>Allocated: <b class="tabular-nums">{money(fromCents(allocated))}</b></span>
	<span class:text-red-600={remaining < 0n}>Remaining (advance): <b class="tabular-nums" data-testid="alloc-remaining">{money(fromCents(remaining))}</b></span>
</div>
