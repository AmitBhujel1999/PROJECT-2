<script lang="ts">
	import { api } from '$lib/api/client';
	import type { JournalAccountOption } from '$lib/types';
	import Combobox from '$lib/components/ui/combobox.svelte';

	/**
	 * Searches every account a journal line can post to: chart-of-accounts
	 * ledgers, cash/bank accounts, customers, vendors and expense categories.
	 * `key` is the server's account key, e.g. "ledger:5".
	 */
	let {
		key = $bindable(''),
		label = $bindable(''),
		id,
		invalid = false
	}: { key?: string; label?: string; id: string; invalid?: boolean } = $props();

	type Row = JournalAccountOption & { id: number };
	// The combobox tracks a numeric id; any non-null number means "chosen".
	let picked = $state<number | null>(key ? 1 : null);

	async function load(q: string): Promise<Row[]> {
		const options = await api.get<JournalAccountOption[]>('journal/vouchers/accounts/', { search: q });
		return options.map((o, i) => ({ ...o, id: i + 1 }));
	}
</script>

<Combobox
	{id}
	bind:value={picked}
	bind:label
	{load}
	{invalid}
	getLabel={(o: Row) => o.name}
	placeholder="Search account…"
	onSelect={(o) => (key = o?.key ?? '')}
>
	{#snippet item(o: Row)}
		<div class="flex items-center justify-between gap-3">
			<span class="font-medium">{o.name}</span>
			<span class="text-xs text-muted-foreground">{o.group}</span>
		</div>
	{/snippet}
</Combobox>
