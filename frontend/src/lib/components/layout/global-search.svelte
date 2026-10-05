<script lang="ts">
	import { goto } from '$app/navigation';
	import { Search } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { debounce } from '$lib/utilities/debounce';
	import Spinner from '$lib/components/ui/spinner.svelte';
	import { cn } from '$lib/utils';

	interface Result {
		id: number;
		title: string;
		subtitle: string;
		url: string;
	}
	interface Group {
		type: string;
		count: number;
		results: Result[];
	}

	const LABELS: Record<string, string> = {
		products: 'Products',
		customers: 'Customers',
		vendors: 'Vendors',
		invoices: 'Sales invoices',
		bills: 'Purchase bills',
		receipts: 'Receipts',
		payments: 'Vendor payments',
		expenses: 'Expenses'
	};

	let q = $state('');
	let groups = $state<Group[]>([]);
	let open = $state(false);
	let loading = $state(false);
	let active = $state(0);
	let rid = 0;

	const flat = $derived(groups.flatMap((g) => g.results));

	const run = debounce(async (value: string) => {
		const id = ++rid;
		if (value.trim().length < 2) {
			groups = [];
			loading = false;
			return;
		}
		loading = true;
		try {
			const data = await api.get<{ groups: Group[] }>('search/', { q: value });
			if (id === rid) {
				groups = data.groups;
				active = 0;
			}
		} finally {
			if (id === rid) loading = false;
		}
	}, 300);

	function go(r: Result) {
		open = false;
		q = '';
		groups = [];
		goto(r.url);
	}

	function onKey(e: KeyboardEvent) {
		if (e.key === 'ArrowDown') {
			e.preventDefault();
			active = Math.min(active + 1, flat.length - 1);
		} else if (e.key === 'ArrowUp') {
			e.preventDefault();
			active = Math.max(active - 1, 0);
		} else if (e.key === 'Enter' && flat[active]) {
			e.preventDefault();
			go(flat[active]);
		} else if (e.key === 'Escape') {
			open = false;
		}
	}

	function onGlobalKey(e: KeyboardEvent) {
		if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
			e.preventDefault();
			document.getElementById('global-search')?.focus();
		}
	}
</script>

<svelte:window onkeydown={onGlobalKey} />

<div class="relative max-w-xl">
	<Search class="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
	<input
		id="global-search"
		type="search"
		role="combobox"
		aria-expanded={open && q.length >= 2}
		aria-controls="global-search-results"
		aria-label="Search products, customers, vendors, invoices, bills (Ctrl+K)"
		placeholder="Search products, parties, invoices, SKU, phone, PAN…  (Ctrl+K)"
		autocomplete="off"
		bind:value={q}
		oninput={() => {
			open = true;
			run(q);
		}}
		onfocus={() => (open = true)}
		onblur={() => setTimeout(() => (open = false), 150)}
		onkeydown={onKey}
		class="h-9 w-full rounded-md border border-input bg-muted/40 pl-8 pr-8 text-sm focus-visible:bg-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/60"
	/>
	{#if loading}<Spinner class="absolute right-2.5 top-1/2 size-3.5 -translate-y-1/2 text-muted-foreground" />{/if}
	{#if open && q.trim().length >= 2}
		<div id="global-search-results" role="listbox" class="absolute left-0 right-0 z-50 mt-1 max-h-[70vh] overflow-auto rounded-lg border bg-background p-1 shadow-xl sm:min-w-[28rem]">
			{#if !groups.length}
				<p class="px-3 py-3 text-sm text-muted-foreground">{loading ? 'Searching…' : 'No results'}</p>
			{/if}
			{#each groups as group (group.type)}
				<p class="px-2 pb-1 pt-2 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
					{LABELS[group.type] ?? group.type} <span class="font-normal">({group.count})</span>
				</p>
				{#each group.results as r (group.type + r.id)}
					{@const idx = flat.indexOf(r)}
					<button
						role="option"
						aria-selected={idx === active}
						class={cn('block w-full rounded px-2 py-1.5 text-left text-sm', idx === active ? 'bg-accent' : 'hover:bg-muted')}
						onmousedown={(e) => {
							e.preventDefault();
							go(r);
						}}
						onmouseenter={() => (active = idx)}
					>
						<span class="font-medium">{r.title}</span>
						{#if r.subtitle}<span class="ml-2 text-xs text-muted-foreground">{r.subtitle}</span>{/if}
					</button>
				{/each}
			{/each}
		</div>
	{/if}
</div>
