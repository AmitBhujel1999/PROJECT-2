<script lang="ts">
	import { page } from '$app/state';
	import PaymentDetail from '$lib/components/payments/payment-detail.svelte';
</script>

<svelte:head><title>Receipts · Mero Lekhapal</title></svelte:head>
{#key page.params.id}
	<PaymentDetail kind="receipt" id={page.params.id?.startsWith('p-') ? page.params.id : Number(page.params.id)} />
{/key}
