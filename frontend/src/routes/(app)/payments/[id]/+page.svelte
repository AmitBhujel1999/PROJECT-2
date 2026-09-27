<script lang="ts">
	import { page } from '$app/state';
	import PaymentDetail from '$lib/components/payments/payment-detail.svelte';
</script>

<svelte:head><title>Payments · Accounting</title></svelte:head>
{#key page.params.id}
	<PaymentDetail kind="payment" id={Number(page.params.id)} />
{/key}
