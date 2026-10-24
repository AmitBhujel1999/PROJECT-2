<script lang="ts">
	import { page } from '$app/state';
	import DocumentDetail from '$lib/components/documents/document-detail.svelte';
</script>

<svelte:head><title>Sales · Mero Lekhapal</title></svelte:head>
{#key page.params.id}
	<DocumentDetail kind="sale" id={Number(page.params.id)} />
{/key}
