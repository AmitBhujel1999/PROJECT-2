<script lang="ts">
	import type { Snippet } from 'svelte';
	import { cn } from '$lib/utils';

	let {
		label,
		for: forId,
		error,
		hint,
		required = false,
		class: className,
		children
	}: { label?: string; for?: string; error?: string; hint?: string; required?: boolean; class?: string; children: Snippet } = $props();
</script>

<div class={cn('grid gap-1.5', className)}>
	{#if label}
		<label for={forId} class="text-sm font-medium text-foreground/90">
			{label}{#if required}<span class="text-destructive" aria-hidden="true"> *</span>{/if}
		</label>
	{/if}
	{@render children()}
	{#if error}
		<p class="text-xs text-destructive" role="alert" id={forId ? `${forId}-error` : undefined}>{error}</p>
	{:else if hint}
		<p class="text-xs text-muted-foreground">{hint}</p>
	{/if}
</div>
