<script lang="ts">
	import { page } from '$app/state';
	import { LayoutDashboard, ShoppingCart, Plus, Receipt, Menu, Truck, HandCoins, Wallet, Package, UserPlus, X } from '@lucide/svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { cn } from '$lib/utils';

	let sheetOpen = $state(false);

	const tabs = $derived(
		[
			{ label: 'Home', href: '/dashboard', icon: LayoutDashboard, perm: 'dashboard.view' },
			{ label: 'Sales', href: '/sales', icon: ShoppingCart, perm: 'sales.view' },
			{ label: 'Expenses', href: '/expenses', icon: Receipt, perm: 'expenses.view' },
			{ label: 'More', href: '/more', icon: Menu }
		].filter((t) => !t.perm || auth.can(t.perm))
	);
	const actions = $derived(
		[
			{ label: 'Sale', href: '/sales/new', icon: ShoppingCart, perm: 'sales.create', color: 'bg-primary' },
			{ label: 'Purchase', href: '/purchases/new', icon: Truck, perm: 'purchases.create', color: 'bg-orange-500' },
			{ label: 'Receipt', href: '/receipts/new', icon: HandCoins, perm: 'receipts.create', color: 'bg-emerald-600' },
			{ label: 'Payment', href: '/payments/new', icon: Wallet, perm: 'payments.create', color: 'bg-orange-700' },
			{ label: 'Expense', href: '/expenses/new', icon: Receipt, perm: 'expenses.create', color: 'bg-slate-500' },
			{ label: 'Product', href: '/products?new=1', icon: Package, perm: 'products.manage', color: 'bg-primary' },
			{ label: 'Customer', href: '/customers?new=1', icon: UserPlus, perm: 'parties.create', color: 'bg-emerald-600' }
		].filter((a) => auth.can(a.perm))
	);

	function active(href: string) {
		const path = page.url.pathname;
		if (href === '/more') return path === '/more' || path === '/search';
		return path === href || (path.startsWith(href + '/') && !path.startsWith('/expenses/categories'));
	}

	$effect(() => {
		page.url.pathname;
		sheetOpen = false;
	});
</script>

<nav class="m-tabs no-print" aria-label="Bottom navigation">
	{#each tabs.slice(0, 2) as t (t.href)}
		<a href={t.href} class={cn('m-tab', active(t.href) && 'on')} aria-current={active(t.href) ? 'page' : undefined}><t.icon class="size-5" />{t.label}</a>
	{/each}
	{#if actions.length}
		<button type="button" class="m-tab m-plus" onclick={() => (sheetOpen = true)} aria-label="Create new" data-testid="mobile-new"><span><Plus class="size-6" /></span>New</button>
	{/if}
	{#each tabs.slice(2) as t (t.href)}
		<a href={t.href} class={cn('m-tab', active(t.href) && 'on')} aria-current={active(t.href) ? 'page' : undefined}><t.icon class="size-5" />{t.label}</a>
	{/each}
</nav>

{#if sheetOpen}
	<div class="fixed inset-0 z-50" role="dialog" aria-modal="true" aria-label="Create new">
		<button class="absolute inset-0 bg-black/40" aria-label="Close" onclick={() => (sheetOpen = false)}></button>
		<div class="absolute inset-x-0 bottom-0 rounded-t-2xl bg-background px-4 pb-6 pt-2 shadow-2xl">
			<div class="mx-auto mb-2 h-1 w-10 rounded-full bg-muted-foreground/30"></div>
			<div class="mb-3 flex items-center justify-between">
				<h2 class="font-semibold">Create new</h2>
				<button class="rounded p-1 text-muted-foreground" onclick={() => (sheetOpen = false)} aria-label="Close"><X class="size-5" /></button>
			</div>
			<div class="grid grid-cols-4 gap-3">
				{#each actions as a (a.href)}
					<a href={a.href} class="grid justify-items-center gap-1 text-center text-[11px] font-semibold">
						<span class={cn('grid size-12 place-items-center rounded-2xl text-white', a.color)}><a.icon class="size-5" /></span>{a.label}
					</a>
				{/each}
			</div>
		</div>
	</div>
{/if}
