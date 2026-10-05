<script lang="ts">
	import type { Component } from 'svelte';
	import { cn } from '$lib/utils';

	let {
		label,
		value,
		hint,
		icon: Icon,
		tone = 'default',
		href
	}: { label: string; value: string; hint?: string; icon?: Component<{ class?: string }>; tone?: 'default' | 'danger' | 'warning' | 'success'; href?: string } = $props();

	const toneClass = { default: 'text-primary bg-primary/10', danger: 'text-red-600 bg-red-50', warning: 'text-amber-700 bg-amber-50', success: 'text-emerald-700 bg-emerald-50' };
</script>

<svelte:element this={href ? 'a' : 'div'} {href} class={cn('flex items-start gap-3 rounded-xl border bg-card p-4 shadow-xs', href && 'transition-colors hover:border-primary/40')}>
	{#if Icon}
		<div class={cn('stat-icon rounded-lg p-2', toneClass[tone])}><Icon class="size-5" /></div>
	{/if}
	<div class="min-w-0">
		<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
		<p class={cn('mt-1 truncate text-lg font-semibold tabular-nums', tone === 'danger' && 'text-red-600')}>{value}</p>
		{#if hint}<p class="text-xs text-muted-foreground">{hint}</p>{/if}
	</div>
</svelte:element>
