<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import type { ChartConfiguration } from 'chart.js';
	import {
		ShoppingCart,
		Truck,
		HandCoins,
		Wallet,
		Receipt,
		Boxes,
		ArrowDownToLine,
		AlarmClock,
		ArrowUpFromLine,
		TriangleAlert,
		Users,
		Building2,
		Plus
	} from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { money, qty, date as fmtDate, today, addDays } from '$lib/utilities/format';
	import { SERIES, GRID, AXIS, axisNumber } from '$lib/utilities/chart';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import StatCard from '$lib/components/ui/stat-card.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Chart from '$lib/components/ui/chart.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import Spinner from '$lib/components/ui/spinner.svelte';

	interface Dashboard {
		start_date: string;
		end_date: string;
		granularity: 'day' | 'month';
		cards: Record<string, any>;
		trend: { date: string; sales: string; purchases: string; receipts: string; payments: string; expenses: string }[];
		top_products: { product_id: number; name: string; sku_code: string; quantity: string; amount: string }[];
		stock_value_by_product: { product_id: number; name: string; value: string; quantity: string }[];
		low_stock: { id: number; name: string; sku_code: string; current_stock: string; reorder_level: string; unit: string; stock_status: string }[];
		recent_sales: { id: number; number: string; date: string; party_name: string; total_amount: string; payment_status: string }[];
	}

	let start = $state(addDays(today(), -29));
	let end = $state(today());

	const query = createQuery(() => ({
		queryKey: ['dashboard', start, end],
		queryFn: () => api.get<Dashboard>('dashboard/', { start_date: start, end_date: end })
	}));

	const d = $derived(query.data);
	const c = $derived(d?.cards ?? {});
	const cur = $derived(auth.currency);
	const finance = $derived(auth.can('reports.view'));

	function preset(days: number) {
		end = today();
		start = addDays(end, -(days - 1));
	}

	const labels = $derived(
		(d?.trend ?? []).map((t) => (d?.granularity === 'month' ? t.date.slice(0, 7) : fmtDate(t.date).slice(0, 5)))
	);
	const scales = {
		x: { grid: { display: false }, ticks: { color: AXIS, maxRotation: 0, autoSkipPadding: 12, font: { size: 11 } } },
		y: { grid: { color: GRID }, border: { display: false }, ticks: { color: AXIS, callback: axisNumber, font: { size: 11 } } }
	};

	const trendConfig = $derived<ChartConfiguration>({
		type: 'line',
		data: {
			labels,
			datasets: [
				{ label: 'Sales', data: (d?.trend ?? []).map((t) => Number(t.sales)), borderColor: SERIES[0], backgroundColor: SERIES[0], borderWidth: 2, pointRadius: 0, pointHoverRadius: 4, tension: 0.25 },
				{ label: 'Purchases', data: (d?.trend ?? []).map((t) => Number(t.purchases)), borderColor: SERIES[1], backgroundColor: SERIES[1], borderWidth: 2, pointRadius: 0, pointHoverRadius: 4, tension: 0.25 }
			]
		},
		options: { scales }
	});

	const cashConfig = $derived<ChartConfiguration>({
		type: 'bar',
		data: {
			labels,
			datasets: [
				{ label: 'Receipts', data: (d?.trend ?? []).map((t) => Number(t.receipts)), backgroundColor: SERIES[2], borderRadius: 3, maxBarThickness: 18 },
				{ label: 'Payments', data: (d?.trend ?? []).map((t) => Number(t.payments)), backgroundColor: SERIES[1], borderRadius: 3, maxBarThickness: 18 },
				...(c.todays_expenses !== undefined
					? [{ label: 'Expenses', data: (d?.trend ?? []).map((t) => Number(t.expenses)), backgroundColor: SERIES[0], borderRadius: 3, maxBarThickness: 18 }]
					: [])
			]
		},
		options: { scales }
	});

	function hbar(items: { name: string; value: number }[], seriesLabel: string, color: string): ChartConfiguration {
		return {
			type: 'bar',
			data: { labels: items.map((i) => i.name), datasets: [{ label: seriesLabel, data: items.map((i) => i.value), backgroundColor: color, borderRadius: 4, maxBarThickness: 16 }] },
			options: {
				indexAxis: 'y',
				scales: {
					x: { grid: { color: GRID }, border: { display: false }, ticks: { color: AXIS, callback: axisNumber, font: { size: 11 } } },
					y: { grid: { display: false }, ticks: { color: AXIS, font: { size: 11 } } }
				},
				plugins: { legend: { display: false } }
			}
		};
	}
	const topConfig = $derived(hbar((d?.top_products ?? []).map((p) => ({ name: p.name, value: Number(p.amount) })), 'Net sales', SERIES[0]));
	const stockConfig = $derived(hbar((d?.stock_value_by_product ?? []).map((p) => ({ name: p.name, value: Number(p.value) })), 'Stock value (cost)', SERIES[0]));
</script>

<svelte:head><title>Dashboard · Accounting</title></svelte:head>

<PageHeader title="Dashboard" description="Business overview for {fmtDate(start)} – {fmtDate(end)}">
	{#snippet actions()}
		<div class="flex flex-wrap items-center gap-2">
			<Button size="sm" variant="outline" onclick={() => preset(7)}>7D</Button>
			<Button size="sm" variant="outline" onclick={() => preset(30)}>30D</Button>
			<Button size="sm" variant="outline" onclick={() => preset(90)}>90D</Button>
			<Button size="sm" variant="outline" onclick={() => preset(365)}>1Y</Button>
			<Input type="date" bind:value={start} class="h-8 w-36" aria-label="Start date" />
			<span class="text-muted-foreground">–</span>
			<Input type="date" bind:value={end} class="h-8 w-36" aria-label="End date" />
			{#if auth.can('sales.create')}<Button size="sm" href="/sales/new"><Plus />New sale</Button>{/if}
		</div>
	{/snippet}
</PageHeader>

{#if query.isPending}
	<div class="flex items-center gap-2 py-16 text-muted-foreground"><Spinner /> Loading dashboard…</div>
{:else if query.isError}
	<p class="rounded-md bg-red-50 p-4 text-red-700">{query.error.message}</p>
{:else if d}
	<div class="grid grid-cols-2 gap-2 sm:gap-3 xl:grid-cols-4" data-testid="dashboard-cards">
		<StatCard label="Today's Sales" value={`${cur} ${money(c.todays_sales)}`} hint={`Period: ${money(c.period_sales)}`} icon={ShoppingCart} href="/sales" />
		<StatCard label="Today's Purchases" value={`${cur} ${money(c.todays_purchases)}`} hint={`Period: ${money(c.period_purchases)}`} icon={Truck} href="/purchases" />
		<StatCard label="Today's Receipts" value={`${cur} ${money(c.todays_receipts)}`} hint={`Period: ${money(c.period_receipts)}`} icon={HandCoins} tone="success" />
		<StatCard label="Today's Payments" value={`${cur} ${money(c.todays_payments)}`} hint={`Period: ${money(c.period_payments)}`} icon={Wallet} />
		{#if c.todays_expenses !== undefined}
			<StatCard label="Today's Expenses" value={`${cur} ${money(c.todays_expenses)}`} hint={`Period: ${money(c.period_expenses)}`} icon={Receipt} href="/expenses" />
		{/if}
		{#if finance}
			<StatCard label="Receivables" value={`${cur} ${money(c.receivables)}`} hint={`Advances: ${money(c.customer_advances)}`} icon={ArrowDownToLine} href="/receivables/aging" />
			<StatCard label="Overdue Receivables" value={`${cur} ${money(c.overdue_receivables)}`} icon={AlarmClock} tone="danger" href="/reports/receivables" />
			<StatCard label="Payables" value={`${cur} ${money(c.payables)}`} hint={`Advances: ${money(c.vendor_advances)}`} icon={ArrowUpFromLine} href="/payables/aging" />
			<StatCard label="Overdue Payables" value={`${cur} ${money(c.overdue_payables)}`} icon={AlarmClock} tone="warning" href="/reports/payables" />
		{/if}
		<StatCard label="Current Stock Value" value={`${cur} ${money(c.stock_value)}`} hint={`Retail: ${money(c.stock_retail_value)}`} icon={Boxes} href="/inventory" />
		<StatCard label="Low Stock Items" value={String(c.low_stock_count)} icon={TriangleAlert} tone={c.low_stock_count ? 'warning' : 'success'} href="/inventory?status=LOW_STOCK" />
		<StatCard label="Total Customers" value={String(c.total_customers)} icon={Users} href="/customers" />
		<StatCard label="Total Vendors" value={String(c.total_vendors)} icon={Building2} href="/vendors" />
	</div>

	<div class="mt-4 grid grid-cols-1 gap-4 xl:grid-cols-2 [&>*]:min-w-0">
		<Card title="Sales vs purchase trend" description={d.granularity === 'month' ? 'Monthly totals incl. VAT' : 'Daily totals incl. VAT'}>
			<Chart config={trendConfig} label="Line chart of sales and purchases over the selected period" />
		</Card>
		<Card title="Cash in vs cash out" description="Received from customers, paid to vendors{c.todays_expenses !== undefined ? ' and spent on expenses' : ''}">
			<Chart config={cashConfig} label="Bar chart of customer receipts, vendor payments and expenses" />
		</Card>
		<Card title="Top selling products" description="By net sales (excl. VAT) in the period">
			{#if d.top_products.length}
				<Chart config={topConfig} height={Math.max(160, d.top_products.length * 30)} label="Top selling products by net sales" />
			{:else}<p class="py-8 text-center text-sm text-muted-foreground">No sales in this period.</p>{/if}
		</Card>
		<Card title="Stock value by product" description="Top 10 products by stock value at cost">
			<Chart config={stockConfig} height={Math.max(160, d.stock_value_by_product.length * 30)} label="Stock value by product" />
		</Card>
	</div>

	<div class="mt-4 grid grid-cols-1 gap-4 xl:grid-cols-2 [&>*]:min-w-0">
		<Card title="Low stock alerts" bodyClass="p-0">
			<div class="overflow-x-auto">
				<table class="table-base">
					<thead><tr><th>Product</th><th class="num">Stock</th><th class="num">Reorder</th><th>Status</th></tr></thead>
					<tbody>
						{#each d.low_stock as p (p.id)}
							<tr>
								<td><a class="font-medium hover:underline" href="/products/{p.id}">{p.name}</a> <span class="text-xs text-muted-foreground">{p.sku_code}</span></td>
								<td class="num">{qty(p.current_stock)} {p.unit}</td>
								<td class="num">{qty(p.reorder_level)}</td>
								<td><StatusBadge status={p.stock_status} /></td>
							</tr>
						{:else}
							<tr><td colspan="4" class="py-6 text-center text-muted-foreground">All products are above reorder level.</td></tr>
						{/each}
					</tbody>
				</table>
			</div>
		</Card>
		<Card title="Recent sales" bodyClass="p-0">
			<div class="overflow-x-auto">
				<table class="table-base">
					<thead><tr><th>Invoice</th><th>Date</th><th>Customer</th><th class="num">Total</th><th>Status</th></tr></thead>
					<tbody>
						{#each d.recent_sales as s (s.id)}
							<tr>
								<td><a class="font-medium text-primary hover:underline" href="/sales/{s.id}">{s.number}</a></td>
								<td>{fmtDate(s.date)}</td>
								<td>{s.party_name}</td>
								<td class="num">{money(s.total_amount)}</td>
								<td><StatusBadge status={s.payment_status} /></td>
							</tr>
						{:else}
							<tr><td colspan="5" class="py-6 text-center text-muted-foreground">No sales yet.</td></tr>
						{/each}
					</tbody>
				</table>
			</div>
		</Card>
	</div>
{/if}
