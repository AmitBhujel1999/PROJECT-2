<script lang="ts">
	import { page } from '$app/state';
	import PaymentDetail from '$lib/components/payments/payment-detail.svelte';
</script>

<svelte:head><title>Receipts · Accounting</title></svelte:head>
{#key page.params.id}
	<PaymentDetail kind="receipt" id={Number(page.params.id)} />
{/key}
