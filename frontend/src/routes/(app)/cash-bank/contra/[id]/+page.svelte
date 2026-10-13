<script lang="ts">
	import { page } from '$app/state';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { Ban } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { money, date as fmtDate, dateTime } from '$lib/utilities/format';
	import type { Contra } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';

	const id = $derived(Number(page.params.id));
	const client = useQueryClient();
	const q = createQuery(() => ({ queryKey: ['contras', id], queryFn: () => api.get<Contra>(`cash-bank/contras/${id}/`) }));
	const c = $derived(q.data);
	let cancelOpen = $state(false);

	async function cancel(reason: string) {
		const res = await api.post<Contra>(`cash-bank/contras/${id}/cancel/`, { reason });
		toast.success(res.message);
		client.setQueryData(['contras', id], res.data);
	}
</script>

<svelte:head><title>Contra · Accounting</title></svelte:head>

{#if c}
	<PageHeader title="Contra {c.contra_number}" description={fmtDate(c.date)} back={{ href: '/cash-bank/contra', label: 'Contra entries' }}>
		{#snippet actions()}
			<StatusBadge status={c.status} />
			{#if c.status === 'ACTIVE' && auth.can('contra.cancel')}<Button variant="destructive" onclick={() => (cancelOpen = true)}><Ban />Cancel</Button>{/if}
		{/snippet}
	</PageHeader>

	{#if c.status === 'CANCELLED'}
		<p class="mb-4 rounded-md border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-800">Cancelled {dateTime(c.cancelled_at)} by {c.cancelled_by_name}: {c.cancel_reason}. It no longer counts in the account books.</p>
	{/if}

	<div class="mb-4 grid grid-cols-1 gap-3 sm:grid-cols-3">
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">From</p><a class="font-semibold text-primary hover:underline" href="/cash-bank/{c.from_account}">{c.from_account_name}</a></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">To</p><a class="font-semibold text-primary hover:underline" href="/cash-bank/{c.to_account}">{c.to_account_name}</a></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Amount ({auth.currency})</p><p class="text-lg font-semibold tabular-nums">{money(c.amount)}</p></div>
	</div>

	<Card title="Details">
		<dl class="grid gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
			<div><dt class="text-xs text-muted-foreground">Reference no.</dt><dd>{c.reference_number || '—'}</dd></div>
			<div><dt class="text-xs text-muted-foreground">Recorded by</dt><dd>{c.created_by_name} · {dateTime(c.created_at)}</dd></div>
			{#if c.notes}<div class="sm:col-span-2"><dt class="text-xs text-muted-foreground">Notes</dt><dd class="whitespace-pre-line">{c.notes}</dd></div>{/if}
		</dl>
	</Card>

	<ConfirmDialog bind:open={cancelOpen} title="Cancel {c.contra_number}?" requireReason destructive confirmLabel="Cancel contra"
		message="The entry is kept for audit but no longer counts in either account's book." onConfirm={cancel} />
{:else if q.isError}
	<h1 class="text-xl font-semibold">Not available</h1>
	<p class="mt-1 text-destructive">{q.error.message}</p>
{/if}
