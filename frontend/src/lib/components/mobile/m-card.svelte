<script lang="ts">
	import type { Snippet } from 'svelte';
	import { cn } from '$lib/utils';

	/** One row of a list, shown as a tappable card on phones. */
	let {
		href,
		title,
		subtitle,
		meta,
		amount,
		amountClass,
		muted = false,
		leading,
		badge
	}: {
		href?: string;
		title: string;
		subtitle?: string;
		meta?: string;
		amount?: string;
		amountClass?: string;
		muted?: boolean;
		leading?: Snippet;
		badge?: Snippet;
	} = $props();
</script>

<svelte:element this={href ? 'a' : 'div'} {href} class={cn('m-card', muted && 'opacity-60')}>
	{#if leading}{@render leading()}{/if}
	<div class="min-w-0 flex-1">
		<p class="truncate text-[13px] font-semibold text-primary">{title}</p>
		{#if subtitle}<p class="truncate text-[13px]">{subtitle}</p>{/if}
		{#if meta}<p class="truncate text-[11px] text-muted-foreground">{meta}</p>{/if}
	</div>
	<div class="shrink-0 text-right">
		{#if amount !== undefined}<p class={cn('font-semibold tabular-nums', amountClass)}>{amount}</p>{/if}
		{#if badge}<div class="mt-0.5">{@render badge()}</div>{/if}
	</div>
</svelte:element>
