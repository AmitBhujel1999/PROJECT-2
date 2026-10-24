<script lang="ts">
	import { ChevronRight, CircleAlert } from '@lucide/svelte';
	import { api, ApiError } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { money, today } from '$lib/utilities/format';
	import { fiscalYearStart } from '$lib/utilities/nepali-date';
	import type { TrialBalance, TrialBalanceRow } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import ExportButtons from '$lib/components/ui/export-buttons.svelte';
	import { cn } from '$lib/utils';

	let start = $state(fiscalYearStart(today()));
	let end = $state(today());
	let d = $state<TrialBalance | null>(null);
	let error = $state<string | null>(null);
	let loading = $state(false);
	let open = $state<Record<string, boolean>>({});
	const query = $derived({ start_date: start || undefined, end_date: end || undefined });
	const allOpen = $derived(!!d && d.groups.every((g) => open[g.key]));

	$effect(() => {
		load(query);
	});

	async function load(q: Record<string, string | undefined>) {
		loading = true;
		error = null;
		try {
			d = await api.get<TrialBalance>('reports/trial-balance/', q);
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not load the trial balance.';
		} finally {
			loading = false;
		}
	}

	function toggleAll() {
		const next = !allOpen;
		open = Object.fromEntries((d?.groups ?? []).map((g) => [g.key, next]));
	}

	function href(r: TrialBalanceRow): string | null {
		switch (r.link) {
			case 'ledger':
				return r.id ? `/journal/ledgers/${r.id}` : null;
			case 'cash':
				return r.id ? `/cash-bank/${r.id}` : null;
			case 'customer':
				return `/customers/${r.id}`;
			case 'vendor':
				return `/vendors/${r.id}`;
			case 'expense-category':
				return `/reports/expenses?category=${r.id}`;
			case 'stock':
				return '/reports/stock';
			case 'sales':
				return '/reports/sales';
			case 'purchases':
				return '/reports/purchases';
			case 'profit-loss':
				return '/reports/profit-loss';
		}
		return null;
	}

	const show = (v: string) => (Number(v) ? money(v) : '');
</script>

<svelte:head><title>Trial Balance · Mero Lekhapal</title></svelte:head>

<PageHeader title="Trial Balance" description="Closing balance of every account in Debit and Credit columns. Income and expenses are for the period." />

<Card bodyClass="p-0" class="mb-4">
	<div class="flex flex-wrap items-end justify-between gap-2 p-3 no-print">
		<div class="flex flex-wrap items-end gap-2">
			<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-8 w-40" /></label>
			<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-8 w-40" /></label>
			<Button variant="outline" size="sm" onclick={toggleAll} data-testid="tb-toggle-all">{allOpen ? 'Collapse all' : 'Expand all'}</Button>
		</div>
		<div class="flex gap-2"><ExportButtons path="reports/trial-balance/" query={{ ...query, detailed: allOpen ? '1' : undefined }} /></div>
	</div>
</Card>

{#if error}<p class="mb-4 text-sm text-destructive">{error}</p>{/if}
{#if d && Number(d.difference) !== 0}
	<p class="mb-4 flex items-center gap-2 rounded-md border border-amber-300 bg-amber-50 px-4 py-2 text-sm text-amber-900">
		<CircleAlert class="size-4" />Debit and credit differ by {money(d.difference)}. Please report this; it should never happen.
	</p>
{/if}

{#if d}
	<Card title="Trial Balance ({auth.currency})" bodyClass="p-0" class={cn(loading && 'opacity-60')}>
		<div class="overflow-x-auto">
			<table class="table-base statement" data-testid="trial-balance">
				<thead><tr><th>Particulars</th><th class="num w-40">Debit</th><th class="num w-40">Credit</th></tr></thead>
				<tbody>
					{#each d.groups as g (g.key)}
						<tr class="cursor-pointer font-semibold hover:bg-muted/40" onclick={() => (open[g.key] = !open[g.key])} data-testid="tb-group-{g.key}">
							<td>
								<span class="inline-flex items-center gap-1">
									<ChevronRight class={cn('size-4 transition-transform no-print', open[g.key] && 'rotate-90')} />
									{g.label}{#if g.count > 1}<span class="font-normal text-muted-foreground">({g.count})</span>{/if}
								</span>
							</td>
							<td class="num tabular-nums">{show(g.debit)}</td>
							<td class="num tabular-nums">{show(g.credit)}</td>
						</tr>
						{#if open[g.key]}
							{#each g.rows as r, i (i)}
								{@const link = href(r)}
								<tr class="text-muted-foreground">
									<td class="pl-12">{#if link}<a class="text-primary hover:underline" href={link}>{r.name}</a>{:else}{r.name}{/if}</td>
									<td class="num tabular-nums">{show(r.debit)}</td>
									<td class="num tabular-nums">{show(r.credit)}</td>
								</tr>
							{/each}
						{/if}
					{/each}
					<tr class="border-t-2 bg-muted/50 font-semibold">
						<td>Total</td>
						<td class="num tabular-nums">{money(d.total_debit)}</td>
						<td class="num tabular-nums">{money(d.total_credit)}</td>
					</tr>
				</tbody>
			</table>
		</div>
	</Card>
	<p class="mt-3 text-xs text-muted-foreground">
		Closing stock is not listed: opening stock, purchases and sales stand for it, as in Tally/Busy. Profit before the period is shown in Capital as "Profit & Loss A/c".
	</p>
{/if}
