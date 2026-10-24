<script lang="ts">
	import { CircleAlert, Save } from '@lucide/svelte';
	import { beforeNavigate } from '$app/navigation';
	import { api, ApiError } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { money } from '$lib/utilities/format';
	import type { OpeningBalances, OpeningRow, OpeningSection, OpeningStockRow } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Tabs from '$lib/components/ui/tabs.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import { cn } from '$lib/utils';

	const SECTIONS: { value: OpeningSection; label: string }[] = [
		{ value: 'cash', label: 'Cash & Bank' },
		{ value: 'ledgers', label: 'Ledgers' },
		{ value: 'customers', label: 'Customers' },
		{ value: 'vendors', label: 'Vendors' },
		{ value: 'stock', label: 'Stock' }
	];
	const SIDES = [
		{ value: 'DR', label: 'Dr' },
		{ value: 'CR', label: 'Cr' }
	];
	const HINTS: Record<OpeningSection, string> = {
		cash: 'Money in each cash, bank or wallet account on the day the books start.',
		ledgers: 'Capital, loans, fixed assets and other chart-of-accounts balances. Enter capital here to clear the difference.',
		customers: 'Dr = the customer owes you. Cr = advance received from the customer.',
		vendors: 'Cr = you owe the vendor. Dr = advance paid to the vendor.',
		stock: 'Opening quantity and cost per unit. The rate can be set only while the quantity is 0; a quantity change is posted on the opening date.'
	};

	let d = $state<OpeningBalances | null>(null);
	/** Editable copy: section -> id -> row. */
	let edit = $state<Record<string, Record<number, Record<string, string>>>>({});
	let tab = $state<OpeningSection>('cash');
	let search = $state('');
	let error = $state<string | null>(null);
	let busy = $state(false);

	load();

	async function load() {
		error = null;
		try {
			apply(await api.get<OpeningBalances>('reports/opening-balances/'));
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not load the opening balances.';
		}
	}

	function apply(data: OpeningBalances) {
		d = data;
		const copy: typeof edit = {};
		for (const s of ['cash', 'ledgers', 'customers', 'vendors'] as const) {
			copy[s] = Object.fromEntries(data[s].map((r) => [r.id, { opening_balance: r.opening_balance, opening_side: r.opening_side }]));
		}
		copy.stock = Object.fromEntries(data.stock.map((r) => [r.id, { opening_stock: String(Number(r.opening_stock)), opening_rate: r.opening_rate }]));
		edit = copy;
	}

	const num = (v: string | undefined) => Number(v) || 0;
	const signed = (amount: string, side: string) => (side === 'CR' ? -num(amount) : num(amount));

	function isChanged(section: OpeningSection, row: OpeningRow | OpeningStockRow): boolean {
		const e = edit[section]?.[row.id];
		if (!e) return false;
		if (section === 'stock') {
			const r = row as OpeningStockRow;
			return num(e.opening_stock) !== num(r.opening_stock) || num(e.opening_rate) !== num(r.opening_rate);
		}
		const r = row as OpeningRow;
		return num(e.opening_balance) !== num(r.opening_balance) || (num(e.opening_balance) > 0 && e.opening_side !== r.opening_side);
	}

	const changes = $derived.by(() => {
		const out: Partial<Record<OpeningSection, Record<string, string | number>[]>> = {};
		if (!d) return out;
		for (const s of SECTIONS) {
			const rows = (d[s.value] as (OpeningRow | OpeningStockRow)[]).filter((r) => isChanged(s.value, r));
			if (rows.length) out[s.value] = rows.map((r) => ({ id: r.id, ...edit[s.value][r.id] }));
		}
		return out;
	});
	const changeCount = $derived(Object.values(changes).reduce((n, rows) => n + (rows?.length ?? 0), 0));

	/** Live Dr/Cr per section, from what is typed. */
	const totals = $derived.by(() => {
		const out = {} as Record<OpeningSection, number>;
		if (!d) return out;
		for (const s of ['cash', 'ledgers', 'customers', 'vendors'] as const) {
			out[s] = d[s].reduce((t, r) => t + signed(edit[s]?.[r.id]?.opening_balance ?? r.opening_balance, edit[s]?.[r.id]?.opening_side ?? r.opening_side), 0);
		}
		// Stock is valued from what is posted, plus the typed changes.
		out.stock =
			num(d.totals.stock.debit) +
			d.stock.reduce((t, r) => {
				const e = edit.stock?.[r.id];
				return e ? t + num(e.opening_stock) * num(e.opening_rate) - num(r.opening_stock) * num(r.opening_rate) : t;
			}, 0);
		return out;
	});
	const net = $derived(Object.values(totals).reduce((a, b) => a + b, 0));
	const fmt = (v: number) => money(Math.abs(v).toFixed(2));

	const rows = $derived.by(() => {
		if (!d) return [];
		const q = search.trim().toLowerCase();
		const list = d[tab] as (OpeningRow | OpeningStockRow)[];
		return q ? list.filter((r) => r.name.toLowerCase().includes(q) || ('sku_code' in r && r.sku_code.toLowerCase().includes(q))) : list;
	});
	const canEdit = $derived(!!d?.can_edit[tab]);

	async function save() {
		if (!changeCount) return;
		busy = true;
		try {
			const res = await api.post<OpeningBalances>('reports/opening-balances/', changes);
			toast.success(res.message);
			apply(res.data);
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Could not save the opening balances.');
		} finally {
			busy = false;
		}
	}

	function onKey(e: KeyboardEvent) {
		if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
			e.preventDefault();
			save();
		}
	}

	beforeNavigate((nav) => {
		if (changeCount && !busy && !confirm('You have unsaved opening balances. Leave without saving?')) nav.cancel();
	});
</script>

<svelte:window onkeydown={onKey} />
<svelte:head><title>Opening Balances · Mero Lekhapal</title></svelte:head>

<PageHeader title="Opening Balances" description="Balances brought forward when the books start. They have no date and count before every entry.">
	{#snippet actions()}
		<Button onclick={save} loading={busy} disabled={!changeCount} data-testid="ob-save"><Save />Save{changeCount ? ` (${changeCount})` : ''}</Button>
	{/snippet}
</PageHeader>

{#if error}<p class="mb-4 text-sm text-destructive">{error}</p>{/if}

{#if d}
	<Tabs tabs={SECTIONS.map((s) => ({ value: s.value, label: s.label }))} bind:value={tab} />
	<div class="mb-3 flex flex-wrap items-center justify-between gap-2">
		<p class="text-xs text-muted-foreground">{HINTS[tab]}</p>
		<SearchInput placeholder="Search…" class="w-56" onSearch={(v) => (search = v)} />
	</div>

	<Card bodyClass="p-0">
		<div class="overflow-x-auto">
			<table class="table-base" data-testid="ob-table">
				<thead>
					<tr>
						<th>{tab === 'stock' ? 'Product' : tab === 'cash' ? 'Account' : tab === 'ledgers' ? 'Ledger' : tab === 'customers' ? 'Customer' : 'Vendor'}</th>
						{#if tab === 'stock'}
							<th class="num w-32">Quantity</th><th class="num w-36">Rate</th><th class="num w-36">Amount</th>
						{:else}
							<th class="w-24">Dr/Cr</th><th class="num w-44">Amount</th>
						{/if}
					</tr>
				</thead>
				<tbody>
					{#each rows as r (r.id)}
						{@const e = edit[tab][r.id]}
						<tr class={cn(isChanged(tab, r) && 'bg-amber-50 dark:bg-amber-950/30', !r.is_active && 'text-muted-foreground')}>
							<td>
								{r.name}
								{#if 'sku_code' in r}<span class="text-xs text-muted-foreground"> · {r.sku_code} · {r.unit}</span>{/if}
								{#if 'group_display' in r && r.group_display}<span class="text-xs text-muted-foreground"> · {r.group_display}</span>{/if}
								{#if 'kind' in r && r.kind}<span class="text-xs text-muted-foreground"> · {r.kind}</span>{/if}
								{#if !r.is_active}<span class="text-xs"> (inactive)</span>{/if}
							</td>
							{#if tab === 'stock'}
								{@const s = r as OpeningStockRow}
								<td><Input type="number" step="0.001" min="0" bind:value={e.opening_stock} disabled={!canEdit} class="h-8 text-right" aria-label="Opening quantity of {r.name}" /></td>
								<td>
									<Input type="number" step="0.01" min="0" bind:value={e.opening_rate} disabled={!canEdit || num(s.opening_stock) > 0} class="h-8 text-right"
										title={num(s.opening_stock) > 0 ? 'Posted opening stock keeps its rate. Set the quantity to 0 and save first to change it.' : ''}
										aria-label="Opening rate of {r.name}" />
								</td>
								<td class="num tabular-nums">{money((num(e.opening_stock) * num(e.opening_rate)).toFixed(2))}</td>
							{:else}
								<td>
									{#if tab === 'cash'}<span class="px-2.5 text-sm">Dr</span>
									{:else}<Select bind:value={e.opening_side} options={SIDES} disabled={!canEdit} class="h-8" aria-label="Dr or Cr for {r.name}" />{/if}
								</td>
								<td><Input type="number" step="0.01" min="0" bind:value={e.opening_balance} disabled={!canEdit} class="h-8 text-right" aria-label="Opening balance of {r.name}" /></td>
							{/if}
						</tr>
					{:else}
						<tr><td colspan="4" class="py-6 text-center text-sm text-muted-foreground">Nothing here yet.</td></tr>
					{/each}
				</tbody>
			</table>
		</div>
	</Card>
	{#if !canEdit}<p class="mt-2 text-xs text-muted-foreground">You can view these balances but not change them.</p>{/if}

	<Card title="Summary ({auth.currency})" class="mt-4" bodyClass="p-0">
		<table class="table-base" data-testid="ob-summary">
			<thead><tr><th>Section</th><th class="num w-44">Debit</th><th class="num w-44">Credit</th></tr></thead>
			<tbody>
				{#each SECTIONS as s (s.value)}
					<tr class={cn(tab === s.value && 'font-semibold')}>
						<td><button class="hover:underline" onclick={() => (tab = s.value)}>{s.label}</button></td>
						<td class="num tabular-nums">{totals[s.value] > 0 ? fmt(totals[s.value]) : ''}</td>
						<td class="num tabular-nums">{totals[s.value] < 0 ? fmt(totals[s.value]) : ''}</td>
					</tr>
				{/each}
				<tr class="border-t-2 bg-muted/50 font-semibold">
					<td>Difference in opening balances</td>
					<td class="num tabular-nums">{net < 0 ? fmt(net) : ''}</td>
					<td class="num tabular-nums">{net > 0 ? fmt(net) : ''}</td>
				</tr>
			</tbody>
		</table>
	</Card>
	{#if Math.abs(net) >= 0.005}
		<p class="mt-3 flex items-start gap-2 text-xs text-muted-foreground">
			<CircleAlert class="mt-0.5 size-3.5 shrink-0" />
			Openings differ by {fmt(net)} {net > 0 ? 'Dr' : 'Cr'}. The balance sheet shows it on the capital side as "Difference in opening balances". Enter the owner's capital as a ledger opening ({net > 0 ? 'Cr' : 'Dr'}) to clear it.
		</p>
	{/if}
{/if}
