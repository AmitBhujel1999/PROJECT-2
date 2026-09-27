<script lang="ts" generics="T extends { id: number }">
	import type { Snippet } from 'svelte';
	import { ChevronsUpDown, X } from '@lucide/svelte';
	import { debounce } from '$lib/utilities/debounce';
	import { cn } from '$lib/utils';
	import Spinner from './spinner.svelte';

	/**
	 * Accessible async combobox (WAI-ARIA combobox + listbox pattern).
	 * Searching happens on the server via `load(query)`.
	 */
	let {
		value = $bindable(null),
		label = $bindable(''),
		load,
		getLabel,
		item: itemSnippet,
		footer,
		placeholder = 'Search…',
		id,
		invalid = false,
		disabled = false,
		onSelect,
		class: className
	}: {
		value?: number | null;
		label?: string;
		load: (q: string) => Promise<T[]>;
		getLabel: (item: T) => string;
		item?: Snippet<[T]>;
		footer?: Snippet<[string]>;
		placeholder?: string;
		id: string;
		invalid?: boolean;
		disabled?: boolean;
		onSelect?: (item: T | null) => void;
		class?: string;
	} = $props();

	let open = $state(false);
	let query = $state('');
	let results = $state<T[]>([]);
	let active = $state(0);
	let loading = $state(false);
	let input: HTMLInputElement | null = $state(null);
	let pos = $state({ top: 0, left: 0, width: 0 });
	let requestId = 0;

	// The list is position:fixed so it is never clipped by scrollable tables.
	function place() {
		if (!input) return;
		const r = input.getBoundingClientRect();
		const width = Math.max(r.width, Math.min(256, window.innerWidth - 16));
		pos = { top: r.bottom + 4, left: Math.max(8, Math.min(r.left, window.innerWidth - width - 8)), width };
	}

	async function search(q: string) {
		const rid = ++requestId;
		loading = true;
		try {
			const items = await load(q);
			if (rid === requestId) {
				results = items;
				active = 0;
			}
		} finally {
			if (rid === requestId) loading = false;
		}
	}
	const debounced = debounce(search, 250);

	function openList() {
		if (disabled) return;
		place();
		open = true;
		search(query);
	}

	function choose(item: T) {
		value = item.id;
		label = getLabel(item);
		query = '';
		open = false;
		onSelect?.(item);
	}

	function clear() {
		value = null;
		label = '';
		query = '';
		onSelect?.(null);
		input?.focus();
	}

	function onKeydown(e: KeyboardEvent) {
		if (e.key === 'ArrowDown') {
			e.preventDefault();
			if (!open) return openList();
			active = Math.min(active + 1, results.length - 1);
		} else if (e.key === 'ArrowUp') {
			e.preventDefault();
			active = Math.max(active - 1, 0);
		} else if (e.key === 'Enter') {
			if (open && results[active]) {
				e.preventDefault();
				choose(results[active]);
			}
		} else if (e.key === 'Escape') {
			open = false;
			query = '';
		}
	}

	$effect(() => {
		if (open) document.getElementById(`${id}-opt-${active}`)?.scrollIntoView({ block: 'nearest' });
	});
</script>

<svelte:window onresize={() => open && place()} />
<svelte:document onscrollcapture={() => open && place()} />

<div class={cn('relative', className)}>
	<div class="relative">
		<input
			bind:this={input}
			{id}
			type="text"
			role="combobox"
			autocomplete="off"
			aria-expanded={open}
			aria-controls="{id}-listbox"
			aria-autocomplete="list"
			aria-activedescendant={open && results[active] ? `${id}-opt-${active}` : undefined}
			aria-invalid={invalid}
			{disabled}
			placeholder={label || placeholder}
			value={open ? query : label}
			oninput={(e) => {
				query = e.currentTarget.value;
				place();
				open = true;
				debounced(query);
			}}
			onfocus={openList}
			onclick={() => !open && openList()}
			onblur={() => setTimeout(() => ((open = false), (query = '')), 150)}
			onkeydown={onKeydown}
			class={cn(
				'h-9 w-full rounded-md border border-input bg-background pl-3 pr-14 text-sm shadow-xs placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/60 disabled:opacity-60',
				label && !open && 'placeholder:text-foreground',
				invalid && 'border-destructive'
			)}
		/>
		<div class="absolute right-1.5 top-1/2 flex -translate-y-1/2 items-center gap-0.5 text-muted-foreground">
			{#if loading}<Spinner class="size-3.5" />{/if}
			{#if value && !disabled}
				<button type="button" class="rounded p-0.5 hover:text-foreground" onclick={clear} aria-label="Clear selection" tabindex="-1"><X class="size-3.5" /></button>
			{/if}
			<ChevronsUpDown class="size-3.5" />
		</div>
	</div>
	{#if open}
		<ul
			id="{id}-listbox"
			role="listbox"
			style="top: {pos.top}px; left: {pos.left}px; width: {pos.width}px"
			class="fixed z-40 max-h-72 overflow-auto rounded-md border bg-background p-1 text-sm shadow-lg"
		>
			{#each results as item, i (item.id)}
				<li
					id="{id}-opt-{i}"
					role="option"
					aria-selected={i === active}
					class={cn('cursor-pointer rounded px-2 py-1.5', i === active ? 'bg-accent text-accent-foreground' : 'hover:bg-muted')}
					onmousedown={(e) => {
						e.preventDefault();
						choose(item);
					}}
					onmouseenter={() => (active = i)}
				>
					{#if itemSnippet}{@render itemSnippet(item)}{:else}{getLabel(item)}{/if}
				</li>
			{:else}
				<li class="px-2 py-2 text-muted-foreground">{loading ? 'Searching…' : 'No matches'}</li>
			{/each}
			{#if footer}
				<li class="mt-1 border-t pt-1" role="presentation">{@render footer(query)}</li>
			{/if}
		</ul>
	{/if}
</div>
