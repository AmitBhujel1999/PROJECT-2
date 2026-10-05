<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { money } from '$lib/utilities/format';
	import type { Paginated } from '$lib/types';

	interface Activity {
		key: string;
		number: string;
		party: string;
		amount: string;
		created_at: string;
		href: string;
	}

	const now = new Date();
	const hour = now.getHours();
	const greeting = hour < 12 ? 'Good morning' : hour < 17 ? 'Good afternoon' : 'Good evening';
	const longDate = now.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });

	const dashboard = createQuery(() => ({
		queryKey: ['home-summary'],
		queryFn: () => api.get<{ cards: Record<string, any> }>('dashboard/'),
		enabled: auth.can('dashboard.view')
	}));
	const c = $derived(dashboard.data?.cards);

	const sources = [
		{ perm: 'sales.view', api: 'sales/', route: '/sales', number: 'invoice_number', party: 'customer_name', amount: 'total_amount' },
		{ perm: 'purchases.view', api: 'purchases/', route: '/purchases', number: 'bill_number', party: 'vendor_name', amount: 'total_amount' },
		{ perm: 'receipts.view', api: 'customer-receipts/', route: '/receipts', number: 'number', party: 'party_name', amount: 'amount' },
		{ perm: 'payments.view', api: 'vendor-payments/', route: '/payments', number: 'number', party: 'party_name', amount: 'amount' },
		{ perm: 'expenses.view', api: 'expenses/', route: '/expenses', number: 'expense_number', party: 'category_name', amount: 'total_amount' }
	];
	const recent = createQuery(() => ({
		queryKey: ['home-recent'],
		queryFn: async () => {
			const lists = await Promise.all(
				sources
					.filter((s) => auth.can(s.perm))
					.map(async (s) => {
						const data = await api.get<Paginated<Record<string, any>>>(s.api, { page_size: 25 });
						return data.results.slice(0, 4).map(
							(r): Activity => ({
								key: `${s.api}${r.id}`,
								number: r[s.number],
								party: r[s.party] ?? '',
								amount: r[s.amount],
								created_at: r.created_at,
								href: `${s.route}/${r.id}`
							})
						);
					})
			);
			return lists.flat().sort((a, b) => b.created_at.localeCompare(a.created_at)).slice(0, 6);
		}
	}));

	const tiles = [
		{ label: 'New sale', hint: 'Alt+T › Sales', href: '/sales/new', perm: 'sales.create', color: 'var(--primary)' },
		{ label: 'New purchase', hint: 'Alt+T › Purchase', href: '/purchases/new', perm: 'purchases.create', color: '#eb6834' },
		{ label: 'Receive payment', hint: 'Alt+T › Receipts', href: '/receipts/new', perm: 'receipts.create', color: '#1baf7a' },
		{ label: 'Pay vendor', hint: 'Alt+T › Payments', href: '/payments/new', perm: 'payments.create', color: '#eb6834' },
		{ label: 'Add expense', hint: 'Alt+T › Expenses', href: '/expenses/new', perm: 'expenses.create', color: 'var(--muted-foreground)' },
		{ label: 'Stock register', hint: 'Alt+D › Inventory', href: '/inventory', perm: 'inventory.view', color: 'var(--primary)' },
		{ label: 'Dashboard', hint: 'Alt+C › Dashboard', href: '/dashboard', perm: 'dashboard.view', color: 'var(--primary)' }
	].filter((t) => auth.can(t.perm));

	function ago(iso: string): string {
		const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000);
		return days <= 0 ? 'today' : days === 1 ? 'yesterday' : `${days} days ago`;
	}
</script>

<svelte:head><title>Home · Accounting</title></svelte:head>

<div class="mx-auto max-w-4xl pt-2 sm:pt-8">
	<h1 class="text-xl font-semibold tracking-tight sm:text-2xl">{greeting}, {auth.user?.display_name}</h1>
	<p class="mt-0.5 text-sm text-muted-foreground">{longDate} · {auth.settings?.business_name ?? ''}</p>

	{#if c}
		<div class="mt-5 grid grid-cols-1 gap-3 sm:grid-cols-3" data-testid="home-summary">
			<div class="rounded-xl border bg-background/85 p-3 shadow-xs">
				<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Today's sales</p>
				<p class="mt-0.5 text-lg font-semibold tabular-nums">{auth.currency} {money(c.todays_sales)}</p>
			</div>
			{#if c.receivables !== undefined}
				<div class="rounded-xl border bg-background/85 p-3 shadow-xs">
					<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Receivables</p>
					<p class="mt-0.5 text-lg font-semibold tabular-nums">{auth.currency} {money(c.receivables)}</p>
				</div>
			{:else}
				<div class="rounded-xl border bg-background/85 p-3 shadow-xs">
					<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Today's receipts</p>
					<p class="mt-0.5 text-lg font-semibold tabular-nums">{auth.currency} {money(c.todays_receipts)}</p>
				</div>
			{/if}
			<a href="/inventory?status=LOW_STOCK" class="rounded-xl border bg-background/85 p-3 shadow-xs hover:border-primary/40">
				<p class="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Low stock items</p>
				<p class="mt-0.5 text-lg font-semibold tabular-nums" class:text-amber-700={c.low_stock_count > 0}>{c.low_stock_count}</p>
			</a>
		</div>
	{/if}

	<h2 class="mb-2 mt-6 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Quick actions</h2>
	<div class="grid grid-cols-2 gap-2.5 sm:grid-cols-3 lg:grid-cols-4">
		{#each tiles as t (t.href)}
			<a href={t.href} class="rounded-xl border bg-background p-3 shadow-xs transition-colors hover:border-primary/50">
				<span class="flex items-center gap-2 font-semibold"><span class="size-2 rounded-full" style="background: {t.color}"></span>{t.label}</span>
				<span class="mt-0.5 block text-[11px] text-muted-foreground">{t.hint}</span>
			</a>
		{/each}
	</div>

	{#if recent.data?.length}
		<h2 class="mb-2 mt-6 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Recent activity</h2>
		<ul class="divide-y overflow-hidden rounded-xl border bg-background shadow-xs">
			{#each recent.data as a (a.key)}
				<li>
					<a href={a.href} class="flex flex-wrap items-center justify-between gap-x-4 gap-y-0.5 px-3 py-2 text-sm hover:bg-muted/50">
						<span><span class="font-medium text-primary">{a.number}</span> · {a.party}</span>
						<span class="text-muted-foreground tabular-nums">{auth.currency} {money(a.amount)} · {ago(a.created_at)}</span>
					</a>
				</li>
			{/each}
		</ul>
	{/if}

	<p class="mt-6 text-xs text-muted-foreground">Tip: press <b>Alt</b> + the underlined letter in the menu bar (e.g. <b>Alt+T</b> for Transactions), or <b>Ctrl+K</b> to search.</p>
</div>
