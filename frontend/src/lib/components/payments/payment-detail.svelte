<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { FileDown, Ban, Link2, Wand2, Unlink } from '@lucide/svelte';
	import { api, ApiError } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { money, date as fmtDate, dateTime, toCents } from '$lib/utilities/format';
	import type { OpenDocument, PaymentDoc } from '$lib/types';
	import { PAY_SIDES, type PayKind } from './config';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Dialog from '$lib/components/ui/dialog.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';
	import AllocationTable from './allocation-table.svelte';

	let { kind, id }: { kind: PayKind; id: number } = $props();
	// svelte-ignore state_referenced_locally
	const side = PAY_SIDES[kind];
	const client = useQueryClient();
	const q = createQuery(() => ({ queryKey: [side.api, id], queryFn: () => api.get<PaymentDoc>(`${side.api}/${id}/`) }));
	const r = $derived(q.data);

	let allocOpen = $state(false);
	let docs = $state<OpenDocument[]>([]);
	let amounts = $state<Record<number, string>>({});
	let busy = $state(false);
	let cancelOpen = $state(false);

	function update(data: PaymentDoc) {
		client.setQueryData([side.api, id], data);
	}

	async function openAllocate() {
		docs = await api.get<OpenDocument[]>(`${side.api}/open-documents/`, { party: r!.party });
		amounts = {};
		allocOpen = true;
	}

	async function allocate() {
		const allocations = Object.entries(amounts).filter(([, v]) => toCents(v) > 0n).map(([document, amount]) => ({ document: Number(document), amount }));
		busy = true;
		try {
			const res = await api.post<PaymentDoc>(`${side.api}/${id}/allocate/`, { allocations });
			toast.success(res.message);
			update(res.data);
			allocOpen = false;
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Allocation failed.');
		} finally {
			busy = false;
		}
	}

	async function autoAllocate() {
		const res = await api.post<PaymentDoc>(`${side.api}/${id}/auto-allocate/`);
		toast.success(res.message);
		update(res.data);
	}

	async function unallocate(allocation: number) {
		try {
			const res = await api.post<PaymentDoc>(`${side.api}/${id}/unallocate/`, { allocation, reason: 'Removed by user' });
			toast.success(res.message);
			update(res.data);
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Failed.');
		}
	}

	async function cancel(reason: string) {
		const res = await api.post<PaymentDoc>(`${side.api}/${id}/cancel/`, { reason });
		toast.success(res.message);
		update(res.data);
	}

	const canEdit = $derived(auth.can(side.createPerm) && r?.status === 'ACTIVE');
</script>

{#if r}
	<PageHeader title="{side.label} {r.number}" description="{r.party_name} · {fmtDate(r.date)}" back={{ href: side.route, label: side.title }}>
		{#snippet actions()}
			<StatusBadge status={r.status} />
			<Button variant="outline" href="/api/{side.api}/{id}/pdf/" download><FileDown />PDF</Button>
			{#if canEdit && Number(r.unallocated_amount) > 0}
				<Button variant="outline" onclick={autoAllocate}><Wand2 />Auto-allocate</Button>
				<Button onclick={openAllocate} data-testid="allocate"><Link2 />Allocate</Button>
			{/if}
			{#if r.status === 'ACTIVE' && auth.can(side.cancelPerm)}<Button variant="destructive" onclick={() => (cancelOpen = true)}><Ban />Cancel</Button>{/if}
		{/snippet}
	</PageHeader>

	{#if r.status === 'CANCELLED'}
		<p class="mb-4 rounded-md border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-800">Cancelled {dateTime(r.cancelled_at)} — {r.cancel_reason}. Allocations were released.</p>
	{/if}

	<div class="mb-4 grid grid-cols-2 gap-3 md:grid-cols-4">
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Amount</p><p class="text-lg font-semibold tabular-nums">{money(r.amount)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Allocated</p><p class="text-lg font-semibold tabular-nums" data-testid="p-allocated">{money(r.allocated_amount)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Unallocated (advance)</p><p class="text-lg font-semibold tabular-nums text-amber-700" data-testid="p-unallocated">{money(r.unallocated_amount)}</p></div>
		<div class="rounded-xl border bg-card p-3"><p class="text-xs text-muted-foreground">Method</p><p class="font-semibold">{r.payment_method_display}</p><p class="text-xs text-muted-foreground">{r.reference_number}</p></div>
	</div>

	<Card title="Allocations" bodyClass="p-0">
		<div class="overflow-x-auto">
			<table class="table-base" data-testid="payment-allocations">
				<thead><tr><th>{side.docLabel}</th><th>{side.docLabel} date</th><th class="num">{side.docLabel} total</th><th>Effective date</th><th class="num">Amount</th><th>Status</th><th></th></tr></thead>
				<tbody>
					{#each r.allocations ?? [] as a (a.id)}
						<tr class:opacity-50={!a.is_active}>
							<td><a class="text-primary hover:underline" href="{side.docRoute}/{a.document_id}">{a.document_number}</a></td>
							<td>{fmtDate(a.document_date)}</td>
							<td class="num">{money(a.document_total)}</td>
							<td>{fmtDate(a.date)}</td>
							<td class="num font-medium" class:line-through={!a.is_active}>{money(a.amount)}</td>
							<td>{#if a.is_active}<StatusBadge status="ACTIVE" />{:else}<span class="text-xs text-muted-foreground">Voided {dateTime(a.voided_at)} · {a.void_reason}</span>{/if}</td>
							<td class="text-right">{#if a.is_active && canEdit}<Button size="sm" variant="ghost" onclick={() => unallocate(a.id)} aria-label="Remove allocation"><Unlink /></Button>{/if}</td>
						</tr>
					{:else}
						<tr><td colspan="7" class="py-6 text-center text-muted-foreground">Not allocated — the full amount is an advance.</td></tr>
					{/each}
				</tbody>
			</table>
		</div>
	</Card>
	{#if r.notes}<p class="mt-3 text-sm text-muted-foreground">Notes: {r.notes}</p>{/if}

	<Dialog bind:open={allocOpen} title="Allocate {r.number}" description="Unallocated: {money(r.unallocated_amount)}" class="max-w-4xl">
		<AllocationTable {docs} bind:amounts available={r.unallocated_amount} docLabel={side.docLabel} docRoute={side.docRoute} />
		{#snippet footer()}
			<Button variant="outline" onclick={() => (allocOpen = false)}>Cancel</Button>
			<Button loading={busy} onclick={allocate} data-testid="confirm-allocate">Allocate</Button>
		{/snippet}
	</Dialog>
	<ConfirmDialog bind:open={cancelOpen} title="Cancel {r.number}?" requireReason destructive confirmLabel="Cancel {side.label.toLowerCase()}"
		message="Allocations will be voided and the {side.docLabel.toLowerCase()}s become outstanding again. The record is kept for audit." onConfirm={cancel} />
{:else if q.isError}
	<p class="text-destructive">{q.error.message}</p>
{/if}
