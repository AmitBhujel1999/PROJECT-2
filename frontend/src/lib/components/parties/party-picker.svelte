<script lang="ts">
	import { Plus } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import type { Paginated, Party, PartyType } from '$lib/types';
	import { SIDES } from './config';
	import Combobox from '$lib/components/ui/combobox.svelte';
	import PartyFormDialog from './party-form-dialog.svelte';

	let {
		type,
		value = $bindable(null),
		label = $bindable(''),
		id,
		invalid = false,
		onSelect
	}: { type: PartyType; value?: number | null; label?: string; id: string; invalid?: boolean; onSelect?: (p: Party | null) => void } = $props();

	const side = $derived(SIDES[type]);
	let addOpen = $state(false);
	let draftName = $state('');

	async function load(q: string) {
		const data = await api.get<Paginated<Party>>(`${side.api}/`, { search: q, is_active: 'true', page_size: 25 });
		return data.results;
	}
</script>

<Combobox {id} bind:value bind:label {load} {invalid} getLabel={(p: Party) => p.name} placeholder="Search {side.label.toLowerCase()} by name, phone, PAN…" {onSelect}>
	{#snippet item(p: Party)}
		<div class="flex items-center justify-between gap-3">
			<span class="font-medium">{p.name}</span>
			<span class="text-xs text-muted-foreground">{[p.phone, p.pan_vat_no && `PAN ${p.pan_vat_no}`, p.credit_terms_display].filter(Boolean).join(' · ')}</span>
		</div>
	{/snippet}
	{#snippet footer(q: string)}
		{#if auth.can('parties.create')}
			<button type="button" class="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-primary hover:bg-muted"
				onmousedown={(e) => { e.preventDefault(); draftName = q; addOpen = true; }}>
				<Plus class="size-4" /> Add {side.label.toLowerCase()}{q ? ` "${q}"` : ''}
			</button>
		{/if}
	{/snippet}
</Combobox>

<PartyFormDialog bind:open={addOpen} {type} initialName={draftName}
	onSaved={(p) => { value = p.id; label = p.name; onSelect?.(p); }} />
