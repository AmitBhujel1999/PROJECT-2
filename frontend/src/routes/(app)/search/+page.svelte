<script lang="ts">
	import { goto } from '$app/navigation';
	import { Search } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { debounce } from '$lib/utilities/debounce';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Spinner from '$lib/components/ui/spinner.svelte';

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
	let loading = $state(false);
	let rid = 0;

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
			if (id === rid) groups = data.groups;
		} finally {
			if (id === rid) loading = false;
		}
	}, 300);
</script>

<svelte:head><title>Search · Accounting</title></svelte:head>

<PageHeader title="Search" back={{ href: '/more', label: 'More' }} />

<div class="relative">
	<Search class="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
	<!-- svelte-ignore a11y_autofocus -->
	<input type="search" bind:value={q} oninput={() => run(q)} autofocus placeholder="Products, parties, invoices, bills, expenses…" aria-label="Search"
		class="h-11 w-full rounded-xl border bg-background pl-9 pr-9 text-sm shadow-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/60" />
	{#if loading}<Spinner class="absolute right-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />{/if}
</div>

{#if q.trim().length >= 2 && !loading && !groups.length}
	<p class="mt-6 text-center text-sm text-muted-foreground">No results for “{q}”.</p>
{/if}

{#each groups as g (g.type)}
	<h2 class="mb-1 mt-4 px-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">{LABELS[g.type] ?? g.type} ({g.count})</h2>
	<ul class="divide-y overflow-hidden rounded-xl border bg-background shadow-xs">
		{#each g.results as r (r.url)}
			<li><button type="button" class="w-full px-3 py-2 text-left text-sm" onclick={() => goto(r.url)}>{r.title}<span class="block text-[11px] text-muted-foreground">{r.subtitle}</span></button></li>
		{/each}
	</ul>
{/each}
