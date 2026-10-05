<script lang="ts">
	import type { Snippet } from 'svelte';

	let { title, description, actions, back }: { title: string; description?: string; actions?: Snippet; back?: { href: string; label: string } } = $props();
</script>

<div class="page-header mb-4 flex flex-wrap items-end justify-between gap-3">
	<div class="min-w-0">
		{#if back}
			<a href={back.href} data-back class="no-print mb-1 inline-block text-xs text-muted-foreground hover:text-foreground">← {back.label}</a>
		{/if}
		<h1 class="truncate text-xl font-semibold tracking-tight sm:text-2xl">{title}</h1>
		{#if description}<p class="mt-0.5 text-sm text-muted-foreground">{description}</p>{/if}
	</div>
	{#if actions}<div class="no-print flex flex-wrap items-center gap-2">{@render actions()}</div>{/if}
</div>
