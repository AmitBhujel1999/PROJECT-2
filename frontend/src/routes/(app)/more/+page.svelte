<script lang="ts">
	import { goto } from '$app/navigation';
	import { ChevronRight, Search } from '@lucide/svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { MENUS, filterMenu, isGroup, type MenuLeaf, type MenuNode } from '$lib/components/layout/menu';
	import PageHeader from '$lib/components/ui/page-header.svelte';

	/** Phone menu: every menu-bar entry the user may open, as grouped lists. */
	function leaves(nodes: MenuNode[]): MenuLeaf[] {
		return nodes.flatMap((n) => (n === 'separator' ? [] : isGroup(n) ? leaves(n.children) : [n]));
	}
	const ORDER = ['Sales', 'Purchase', 'Customer Receipts', 'Vendor Payments', 'Expenses', 'Other transactions', 'Masters', 'Inventory',
		'Accounts Receivable', 'Accounts Payable', 'Reports', 'Users', 'Administration', 'Help'];
	const rank = (title: string) => (ORDER.includes(title) ? ORDER.indexOf(title) : ORDER.length);
	const sections = $derived(
		MENUS.filter((m) => m.label !== 'Company').flatMap((m) => {
			const items = filterMenu(m.items, (p) => auth.can(p));
			const loose = items.filter((n): n is MenuLeaf => n !== 'separator' && !isGroup(n));
			const groups = items.filter(isGroup).map((g) => ({ title: g.label, items: leaves(g.children) }));
			return [...groups, ...(loose.length ? [{ title: m.label === 'Transactions' ? 'Other transactions' : m.label, items: loose }] : [])];
		}).sort((a, b) => rank(a.title) - rank(b.title))
	);
	const company = $derived(leaves(filterMenu(MENUS[0].items, (p) => auth.can(p))).filter((l) => l.label !== 'Dashboard'));

	// Present only inside the Android app (MainActivity's JavaScript bridge).
	const androidApp = (globalThis as { AccountingApp?: { changeServer(): void; server(): string } }).AccountingApp;

	async function pick(leaf: MenuLeaf) {
		if (leaf.action === 'logout') {
			await auth.logout();
			goto('/login');
		}
	}
</script>

<svelte:head><title>More · Accounting</title></svelte:head>

<PageHeader title="More" description="{auth.user?.display_name} · {auth.user?.role}" />

<a href="/search" class="mb-3 flex items-center gap-2 rounded-xl border bg-background px-3 py-2.5 text-sm text-muted-foreground shadow-xs">
	<Search class="size-4" /> Search products, parties, invoices…
</a>

<nav aria-label="Main navigation" class="grid gap-3">
	{#each sections as sec (sec.title)}
		<section>
			<h2 class="mb-1 px-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">{sec.title}</h2>
			<ul class="divide-y overflow-hidden rounded-xl border bg-background shadow-xs">
				{#each sec.items as item (item.href ?? item.label)}
					<li><a href={item.href} class="flex items-center justify-between px-3 py-2.5 text-sm">{item.label}<ChevronRight class="size-4 text-muted-foreground" /></a></li>
				{/each}
			</ul>
		</section>
	{/each}
	<section>
		<h2 class="mb-1 px-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Me</h2>
		<ul class="divide-y overflow-hidden rounded-xl border bg-background shadow-xs">
			{#each company as item (item.label)}
				<li>
					{#if item.href}
						<a href={item.href} class="flex items-center justify-between px-3 py-2.5 text-sm">{item.label}<ChevronRight class="size-4 text-muted-foreground" /></a>
					{:else}
						<button type="button" class="w-full px-3 py-2.5 text-left text-sm text-destructive" onclick={() => pick(item)} data-testid="mobile-logout">{item.label}</button>
					{/if}
				</li>
			{/each}
			{#if androidApp}
				<li>
					<button type="button" class="w-full px-3 py-2.5 text-left text-sm" onclick={() => androidApp.changeServer()}>
						Change server address<span class="block text-[11px] text-muted-foreground">{androidApp.server()}</span>
					</button>
				</li>
			{/if}
		</ul>
	</section>
</nav>
