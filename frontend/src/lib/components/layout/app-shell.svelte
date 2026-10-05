<script lang="ts">
	import type { Snippet } from 'svelte';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { DropdownMenu } from 'bits-ui';
	import { Menu, PanelLeftClose, PanelLeftOpen, X, LogOut, KeyRound, UserRound, ChevronDown } from '@lucide/svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { cn } from '$lib/utils';
	import { NAV } from './nav';
	import GlobalSearch from './global-search.svelte';

	let { children }: { children: Snippet } = $props();

	let mobileOpen = $state(false);
	let collapsed = $state(false);

	// Collapse preference is a harmless UI convenience (no auth data).
	$effect(() => {
		collapsed = localStorage.getItem('sidebar-collapsed') === '1';
	});
	function toggleCollapsed() {
		collapsed = !collapsed;
		localStorage.setItem('sidebar-collapsed', collapsed ? '1' : '0');
	}

	const groups = $derived(
		NAV.map((g) => ({ ...g, items: g.items.filter((i) => !i.perm || auth.can(i.perm)) })).filter((g) => g.items.length)
	);

	function isActive(href: string) {
		const path: string = page.url.pathname;
		if (href === '/inventory') return path === '/inventory';
		if (href === '/settings') return path === '/settings';
		if (href === '/expenses') return path === '/expenses' || (path.startsWith('/expenses/') && !path.startsWith('/expenses/categories'));
		return path === href || path.startsWith(href + '/');
	}

	$effect(() => {
		page.url.pathname;
		mobileOpen = false;
	});

	async function logout() {
		await auth.logout();
		goto('/login');
	}
</script>

{#snippet sidebar(compact: boolean)}
	<div class="flex h-14 shrink-0 items-center gap-2 border-b border-white/10 px-4">
		<div class="grid size-8 shrink-0 place-items-center rounded-md bg-primary font-bold text-white">A</div>
		{#if !compact}
			<div class="min-w-0 leading-tight">
				<p class="truncate text-sm font-semibold text-white">{auth.settings?.business_name ?? 'Accounting'}</p>
				<p class="text-[11px] text-sidebar-muted">Accounting & Inventory</p>
			</div>
		{/if}
	</div>
	<nav class="flex-1 overflow-y-auto px-2 py-3" aria-label="Main navigation">
		{#each groups as group (group.label)}
			{#if group.label && !compact}
				<p class="mb-1 mt-4 px-2 text-[11px] font-semibold uppercase tracking-wider text-sidebar-muted first:mt-0">{group.label}</p>
			{:else if group.label}
				<div class="mx-2 my-2 border-t border-white/10"></div>
			{/if}
			<ul class="grid gap-0.5">
				{#each group.items as item (item.href)}
					<li>
						<a
							href={item.href}
							title={compact ? item.label : undefined}
							aria-current={isActive(item.href) ? 'page' : undefined}
							class={cn(
								'flex items-center gap-2.5 rounded-md px-2 py-1.5 text-sm transition-colors',
								compact && 'justify-center',
								isActive(item.href) ? 'bg-sidebar-accent font-medium text-white' : 'text-sidebar-foreground/80 hover:bg-sidebar-accent/60 hover:text-white'
							)}
						>
							<item.icon class="size-4 shrink-0" />
							{#if !compact}<span class="truncate">{item.label}</span>{/if}
						</a>
					</li>
				{/each}
			</ul>
		{/each}
	</nav>
{/snippet}

<div class="min-h-screen">
	<!-- Desktop / tablet sidebar -->
	<aside
		class={cn(
			'no-print fixed inset-y-0 left-0 z-30 hidden flex-col bg-sidebar text-sidebar-foreground transition-[width] md:flex',
			collapsed ? 'w-16' : 'w-16 lg:w-64'
		)}
	>
		<div class="hidden h-full flex-col lg:flex">{@render sidebar(collapsed)}</div>
		<div class="flex h-full flex-col lg:hidden">{@render sidebar(true)}</div>
	</aside>

	<!-- Mobile drawer -->
	{#if mobileOpen}
		<div class="no-print fixed inset-0 z-40 md:hidden">
			<button class="absolute inset-0 bg-black/50" aria-label="Close navigation" onclick={() => (mobileOpen = false)}></button>
			<aside class="relative flex h-full w-72 max-w-[85vw] flex-col bg-sidebar text-sidebar-foreground shadow-xl" aria-label="Navigation drawer">
				<button class="absolute right-2 top-3 rounded p-1.5 text-white/70 hover:text-white" onclick={() => (mobileOpen = false)} aria-label="Close navigation">
					<X class="size-5" />
				</button>
				{@render sidebar(false)}
			</aside>
		</div>
	{/if}

	<div class={cn('flex min-h-screen min-w-0 flex-col transition-[padding]', collapsed ? 'md:pl-16' : 'md:pl-16 lg:pl-64')}>
		<header class="no-print sticky top-0 z-20 flex h-14 items-center gap-2 border-b bg-background/95 px-3 backdrop-blur sm:px-4">
			<button class="rounded-md p-2 hover:bg-muted md:hidden" onclick={() => (mobileOpen = true)} aria-label="Open navigation" data-testid="mobile-menu">
				<Menu class="size-5" />
			</button>
			<button class="hidden rounded-md p-2 hover:bg-muted lg:block" onclick={toggleCollapsed} aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
				{#if collapsed}<PanelLeftOpen class="size-5" />{:else}<PanelLeftClose class="size-5" />{/if}
			</button>
			<div class="min-w-0 flex-1"><GlobalSearch /></div>
			<DropdownMenu.Root>
				<DropdownMenu.Trigger class="flex items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-muted" data-testid="user-menu">
					<span class="grid size-7 place-items-center rounded-full bg-primary/10 text-xs font-semibold text-primary">
						{(auth.user?.display_name ?? '?').slice(0, 1).toUpperCase()}
					</span>
					<span class="hidden text-left leading-tight sm:block">
						<span class="block max-w-32 truncate font-medium">{auth.user?.display_name}</span>
						<span class="block text-[11px] text-muted-foreground">{auth.user?.role}</span>
					</span>
					<ChevronDown class="size-4 text-muted-foreground" />
				</DropdownMenu.Trigger>
				<DropdownMenu.Portal>
					<DropdownMenu.Content align="end" sideOffset={6} class="z-50 w-52 rounded-md border bg-background p-1 text-sm shadow-lg">
						<DropdownMenu.Item class="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 data-highlighted:bg-muted" onSelect={() => goto('/profile')}>
							<UserRound class="size-4" /> Profile
						</DropdownMenu.Item>
						<DropdownMenu.Item class="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 data-highlighted:bg-muted" onSelect={() => goto('/profile?tab=password')}>
							<KeyRound class="size-4" /> Change password
						</DropdownMenu.Item>
						<DropdownMenu.Separator class="my-1 h-px bg-border" />
						<DropdownMenu.Item class="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-destructive data-highlighted:bg-red-50" onSelect={logout} data-testid="logout">
							<LogOut class="size-4" /> Log out
						</DropdownMenu.Item>
					</DropdownMenu.Content>
				</DropdownMenu.Portal>
			</DropdownMenu.Root>
		</header>
		<main class="print-full mx-auto w-full max-w-[1600px] flex-1 p-3 sm:p-5">
			{@render children()}
		</main>
	</div>
</div>
