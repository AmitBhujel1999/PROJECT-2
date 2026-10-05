<script lang="ts">
	import { page } from '$app/state';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { FileDown, Ban, Printer } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { money, date as fmtDate, dateTime } from '$lib/utilities/format';
	import type { Expense } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';

	const id = $derived(Number(page.params.id));
	const client = useQueryClient();
	const q = createQuery(() => ({ queryKey: ['expenses', id], queryFn: () => api.get<Expense>(`expenses/${id}/`) }));
	const e = $derived(q.data);
	let cancelOpen = $state(false);

	async function cancel(reason: string) {
		const res = await api.post<Expense>(`expenses/${id}/cancel/`, { reason });
		toast.success(res.message);
		client.setQueryData(['expenses', id], res.data);
	}
</script>

<svelte:head><title>Expenses · Accounting</title></svelte:head>

{#if e}
	<PageHeader title="Expense {e.expense_number}" description="{e.category_name} · {fmtDate(e.date)}" back={{ href: '/expenses', label: 'Expenses' }}>
		{#snippet actions()}
			<StatusBadge status={e.status} />
			<Button variant="outline" href="/api/expenses/{id}/pdf/?inline=1" target="_blank"><Printer />Print</Button>
			<Button variant="outline" href="/api/expenses/{id}/pdf/" download><FileDown />PDF</Button>
			{#if e.status === 'ACTIVE' && auth.can('expenses.cancel')}<Button variant="destructive" onclick={() => (cancelOpen = true)}><Ban />Cancel</Button>{/if}
		{/snippet}
	</PageHeader>

	{#if e.status === 'CANCELLED'}
		<p class="mb-4 rounded-md border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-800">Cancelled {dateTime(e.cancelled_at)} by {e.cancelled_by_name} — {e.cancel_reason}. It no longer counts in reports.</p>
	{/if}

	<div class="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4">
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Amount (excl. tax)</p><p class="text-lg font-semibold tabular-nums">{money(e.amount)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">{auth.taxLabel} @ {e.tax_rate}%</p><p class="text-lg font-semibold tabular-nums">{money(e.tax_amount)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Total ({auth.currency})</p><p class="text-lg font-semibold tabular-nums" data-testid="expense-total">{money(e.total_amount)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Method</p><p class="font-semibold">{e.payment_method_display}</p><p class="text-xs text-muted-foreground">{e.reference_number}</p></div>
	</div>

	<Card title="Details">
		<dl class="grid gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
			<div><dt class="text-xs text-muted-foreground">Description</dt><dd>{e.description}</dd></div>
			<div><dt class="text-xs text-muted-foreground">Category</dt><dd>{e.category_name}</dd></div>
			<div>
				<dt class="text-xs text-muted-foreground">Paid to</dt>
				<dd>{#if e.vendor}<a class="text-primary hover:underline" href="/vendors/{e.vendor}">{e.vendor_name}</a>{:else}{e.payee || '—'}{/if}</dd>
			</div>
			<div><dt class="text-xs text-muted-foreground">Recorded by</dt><dd>{e.created_by_name} · {dateTime(e.created_at)}</dd></div>
			{#if e.notes}<div class="sm:col-span-2"><dt class="text-xs text-muted-foreground">Notes</dt><dd class="whitespace-pre-line">{e.notes}</dd></div>{/if}
		</dl>
	</Card>

	<ConfirmDialog bind:open={cancelOpen} title="Cancel {e.expense_number}?" requireReason destructive confirmLabel="Cancel expense"
		message="The expense is kept for audit but no longer counts in reports or the dashboard." onConfirm={cancel} />
{:else if q.isError}
	<h1 class="text-xl font-semibold">Not available</h1>
	<p class="mt-1 text-destructive">{q.error.message}</p>
{/if}
