<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { SIDES } from '$lib/components/parties/config';
	import PartyPicker from '$lib/components/parties/party-picker.svelte';
	import StatementView from '$lib/components/accounts/statement-view.svelte';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';

	const side = SIDES['CUSTOMER'];
	let partyId = $state<number | null>(page.url.searchParams.get('party') ? Number(page.url.searchParams.get('party')) : null);
	let label = $state('');
	const title = `${side.label} Statements`;
</script>

<svelte:head><title>{title} · Accounting</title></svelte:head>
<PageHeader {title} description="Derived from {side.docLabel.toLowerCase()}s, {side.payLabel.toLowerCase()}s and cancellations — balances are never edited manually." />
<Card class="mb-4">
	<div class="max-w-md">
		<label for="ledger-party" class="mb-1 block text-sm font-medium">{side.label}</label>
		<PartyPicker type="CUSTOMER" id="ledger-party" bind:value={partyId} bind:label
			onSelect={(p) => goto(p ? `?party=${p.id}` : '?', { replaceState: true, keepFocus: true, noScroll: true })} />
	</div>
</Card>
{#if partyId}
	{#key partyId}
		<Card bodyClass="p-0"><StatementView type="CUSTOMER" partyId={partyId} /></Card>
	{/key}
{:else}
	<p class="py-10 text-center text-sm text-muted-foreground">Select a {side.label.toLowerCase()} to view.</p>
{/if}
