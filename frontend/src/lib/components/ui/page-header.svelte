<script lang="ts">
	import type { Snippet } from 'svelte';
	import { page } from '$app/state';
	import { ChevronLeft, Search } from '@lucide/svelte';
	import { phone } from '$lib/stores/viewport.svelte';

	let { title, description, actions, back }: { title: string; description?: string; actions?: Snippet; back?: { href: string; label: string } } = $props();
</script>

<div class="page-header mb-4 flex flex-wrap items-end justify-between gap-3">
	{#if phone.current}
		<!-- Phone top bar: back chevron, title, search -->
		<div class="flex w-full items-center gap-2">
			{#if back}
				<a href={back.href} data-back class="no-print -ml-1 rounded p-1" aria-label="Back to {back.label}"><ChevronLeft class="size-5" /></a>
			{:else}
				<span class="grid size-6 shrink-0 place-items-center rounded bg-primary text-xs font-bold text-white">A</span>
			{/if}
			<div class="min-w-0 flex-1">
				<h1 class="truncate font-semibold tracking-tight">{title}</h1>
				{#if description}<p class="truncate">{description}</p>{/if}
			</div>
			{#if page.url.pathname !== '/search'}
				<a href="/search" class="no-print rounded p-1.5 text-white/80" aria-label="Search"><Search class="size-5" /></a>
			{/if}
		</div>
		{#if actions}<div class="no-print flex w-full flex-wrap items-center gap-1.5">{@render actions()}</div>{/if}
	{:else}
		<div class="min-w-0">
			{#if back}
				<a href={back.href} data-back class="no-print mb-1 inline-block text-xs text-muted-foreground hover:text-foreground">← {back.label}</a>
			{/if}
			<h1 class="truncate text-xl font-semibold tracking-tight sm:text-2xl">{title}</h1>
			{#if description}<p class="mt-0.5 text-sm text-muted-foreground">{description}</p>{/if}
		</div>
		{#if actions}<div class="no-print flex flex-wrap items-center gap-2">{@render actions()}</div>{/if}
	{/if}
</div>
