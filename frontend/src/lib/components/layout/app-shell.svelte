<script lang="ts">
	import type { Snippet } from 'svelte';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { DropdownMenu } from 'bits-ui';
	import { Menu, X, LogOut, KeyRound, UserRound, ChevronDown } from '@lucide/svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { cn } from '$lib/utils';
	import { MENUS, filterMenu, nepaliFiscalYear, type MenuLeaf } from './menu';
	import MenuTree from './menu-tree.svelte';
	import GlobalSearch from './global-search.svelte';

	let { children }: { children: Snippet } = $props();

	let openMenu = $state<string | null>(null);
	let mobileOpen = $state(false);

	const menus = $derived(MENUS.map((m) => ({ ...m, items: filterMenu(m.items, (p) => auth.can(p)) })).filter((m) => m.items.length));
	// Home and dashboard keep their own full-width layout; every other page sits in a window.
	const bare = $derived(page.url.pathname === '/' || page.url.pathname === '/dashboard');
	const fiscalYear = nepaliFiscalYear();

	$effect(() => {
		page.url.pathname;
		mobileOpen = false;
		openMenu = null;
	});

	async function logout() {
		await auth.logout();
		goto('/login');
	}

	function pick(leaf: MenuLeaf) {
		openMenu = null;
		mobileOpen = false;
		if (leaf.action === 'logout') logout();
	}

	function label(text: string, key: string) {
		const i = text.toLowerCase().indexOf(key);
		return { before: text.slice(0, i), key: text.slice(i, i + 1), after: text.slice(i + 1) };
	}

	const typing = (el: Element | null) => !!el && (el.matches('input, textarea, select, [contenteditable="true"]') || !!el.closest('[role="listbox"]'));
	const dialogOpen = () => !!document.querySelector('[role="dialog"], [role="alertdialog"]');

	function onKeydown(e: KeyboardEvent) {
		if (e.altKey && !e.ctrlKey && !e.metaKey && /^[a-z]$/i.test(e.key)) {
			const menu = menus.find((m) => m.key === e.key.toLowerCase());
			if (menu) {
				e.preventDefault();
				openMenu = menu.label;
			}
			return;
		}
		if (e.key === 'Escape') {
			if (openMenu || mobileOpen) {
				openMenu = null;
				mobileOpen = false;
				return;
			}
			if (bare || dialogOpen() || typing(document.activeElement)) return;
			const back = document.querySelector<HTMLAnchorElement>('[data-back]');
			goto(back?.getAttribute('href') ?? '/');
			return;
		}
		if (e.key === 'F2' && !dialogOpen()) {
			const add = document.querySelector<HTMLElement>('[data-shortcut="add"], [data-testid^="new-"]');
			if (add) {
				e.preventDefault();
				add.click();
			}
			return;
		}
		if (e.key === 'F5' && !dialogOpen()) {
			e.preventDefault();
			window.print();
		}
	}
</script>

<svelte:window onkeydown={onKeydown} onclick={() => (openMenu = null)} />

<div class="flex min-h-screen flex-col">
	<header class="menubar no-print sticky top-0 z-30 flex h-10 items-center gap-1 px-2 sm:px-3">
		<button class="rounded-md p-1.5 text-white/80 hover:bg-white/10 hover:text-white md:hidden" onclick={() => (mobileOpen = true)} aria-label="Open navigation" data-testid="mobile-menu">
			<Menu class="size-5" />
		</button>
		<a href="/" class="mr-1 grid size-6 shrink-0 place-items-center rounded bg-primary text-xs font-bold text-white" title="Home">A</a>

		<nav class="hidden items-center md:flex" aria-label="Main navigation">
			{#each menus as menu (menu.label)}
				{@const l = label(menu.label, menu.key)}
				<div class="relative">
					<button
						type="button"
						class={cn('menubar-item', openMenu === menu.label && 'is-open')}
						aria-haspopup="true"
						aria-expanded={openMenu === menu.label}
						aria-keyshortcuts="Alt+{menu.key.toUpperCase()}"
						onclick={(e) => {
							e.stopPropagation();
							openMenu = openMenu === menu.label ? null : menu.label;
						}}
						onmouseenter={() => {
							if (openMenu) openMenu = menu.label;
						}}
					>{l.before}<u>{l.key}</u>{l.after}</button>
					{#if openMenu === menu.label}
						<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
						<div class="menu-panel" onclick={(e) => e.stopPropagation()}>
							<MenuTree nodes={menu.items} onPick={pick} />
						</div>
					{/if}
				</div>
			{/each}
		</nav>

		<div class="menubar-search ml-auto w-full max-w-xs min-w-0"><GlobalSearch /></div>

		<DropdownMenu.Root>
			<DropdownMenu.Trigger class="flex items-center gap-2 rounded-md px-2 py-1 text-sm text-white/90 hover:bg-white/10" data-testid="user-menu">
				<span class="grid size-6 place-items-center rounded-full bg-white/15 text-xs font-semibold text-white">
					{(auth.user?.display_name ?? '?').slice(0, 1).toUpperCase()}
				</span>
				<span class="hidden text-left leading-tight lg:block">
					<span class="block max-w-32 truncate text-xs font-medium">{auth.user?.display_name}</span>
					<span class="block text-[10px] text-white/60">{auth.user?.role}</span>
				</span>
				<ChevronDown class="size-4 text-white/60" />
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

	<!-- Mobile drawer: every menu expanded as one tree -->
	{#if mobileOpen}
		<div class="no-print fixed inset-0 z-40 md:hidden">
			<button class="absolute inset-0 bg-black/50" aria-label="Close navigation" onclick={() => (mobileOpen = false)}></button>
			<aside class="relative flex h-full w-80 max-w-[85vw] flex-col bg-background shadow-xl" aria-label="Navigation drawer">
				<div class="menubar flex h-10 shrink-0 items-center justify-between px-3 text-sm font-semibold text-white">
					{auth.settings?.business_name ?? 'Accounting'}
					<button class="rounded p-1 text-white/70 hover:text-white" onclick={() => (mobileOpen = false)} aria-label="Close navigation"><X class="size-5" /></button>
				</div>
				<nav class="flex-1 overflow-y-auto p-3" aria-label="Main navigation">
					{#each menus as menu (menu.label)}
						<p class="mb-1 mt-3 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground first:mt-0">{menu.label}</p>
						<MenuTree nodes={menu.items} expandAll onPick={pick} />
					{/each}
				</nav>
			</aside>
		</div>
	{/if}

	<main class="workspace print-full flex-1 px-3 pb-14 pt-3 sm:px-5 sm:pt-5">
		<div class="relative z-[1] mx-auto w-full max-w-[1600px]">
			{#if bare}
				{@render children()}
			{:else}
				<div class="window print-full">
					<div class="window-body">{@render children()}</div>
					<div class="window-keys no-print" aria-label="Keyboard shortcuts">
						<span><b>F2</b> Add</span><span><b>F5</b> Print</span><span><b>Esc</b> Back</span><span><b>Alt</b>+letter Menu</span><span><b>Ctrl+K</b> Search</span>
					</div>
				</div>
			{/if}
		</div>
		<p class="status-line no-print">{auth.settings?.business_name ?? 'Accounting'} (F.Y. {fiscalYear})</p>
	</main>
</div>
