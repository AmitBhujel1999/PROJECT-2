<script lang="ts">
	import type { Snippet } from 'svelte';
	import { cn } from '$lib/utils';

	let {
		title,
		description,
		actions,
		class: className,
		bodyClass,
		children
	}: { title?: string; description?: string; actions?: Snippet; class?: string; bodyClass?: string; children?: Snippet } = $props();
</script>

<section class={cn('rounded-xl border bg-card text-card-foreground shadow-xs', className)}>
	{#if title || actions}
		<header class="flex flex-wrap items-start justify-between gap-2 border-b px-4 py-3">
			<div>
				{#if title}<h2 class="text-sm font-semibold">{title}</h2>{/if}
				{#if description}<p class="text-xs text-muted-foreground">{description}</p>{/if}
			</div>
			{#if actions}<div class="flex flex-wrap items-center gap-2">{@render actions()}</div>{/if}
		</header>
	{/if}
	<div class={cn('p-4', bodyClass)}>{@render children?.()}</div>
</section>
