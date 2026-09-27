<script lang="ts">
	import { page } from '$app/state';
	import PartyDetail from '$lib/components/parties/party-detail.svelte';
</script>

<svelte:head><title>Customers · Accounting</title></svelte:head>
{#key page.params.id}
	<PartyDetail type="CUSTOMER" id={Number(page.params.id)} />
{/key}
