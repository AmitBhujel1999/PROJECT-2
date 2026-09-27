<script lang="ts">
	import { page } from '$app/state';
	import DocumentDetail from '$lib/components/documents/document-detail.svelte';
</script>

<svelte:head><title>Sales · Accounting</title></svelte:head>
{#key page.params.id}
	<DocumentDetail kind="sale" id={Number(page.params.id)} />
{/key}
