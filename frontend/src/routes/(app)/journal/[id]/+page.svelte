<script lang="ts">
	import { page } from '$app/state';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { FileDown, Ban, Printer } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { money, date as fmtDate, dateTime } from '$lib/utilities/format';
	import type { JournalVoucher } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';

	const id = $derived(Number(page.params.id));
	const client = useQueryClient();
	const q = createQuery(() => ({ queryKey: ['journal', id], queryFn: () => api.get<JournalVoucher>(`journal/vouchers/${id}/`) }));
	const v = $derived(q.data);
	let cancelOpen = $state(false);

	async function cancel(reason: string) {
		const res = await api.post<JournalVoucher>(`journal/vouchers/${id}/cancel/`, { reason });
		toast.success(res.message);
		client.setQueryData(['journal', id], res.data);
	}

	/** Where each kind of account's own ledger lives. */
	function accountHref(account: { key: string; group: string }): string | null {
		const [kind, pk] = account.key.split(':');
		if (kind === 'party') return `/${account.group === 'Vendor' ? 'vendors' : 'customers'}/${pk}`;
		if (kind === 'ledger') return `/journal/ledgers/${pk}`;
		if (kind === 'cash') return `/cash-bank/${pk}`;
		if (kind === 'expense') return `/reports/expenses?category=${pk}`;
		return null;
	}
</script>

<svelte:head><title>Journal Voucher · Mero Lekhapal</title></svelte:head>

{#if v}
	<PageHeader title="Journal Voucher {v.voucher_number}" description={fmtDate(v.date)} back={{ href: '/journal', label: 'Journal vouchers' }}>
		{#snippet actions()}
			<StatusBadge status={v.status} />
			<Button variant="outline" href="/api/journal/vouchers/{id}/pdf/?inline=1" target="_blank"><Printer />Print</Button>
			<Button variant="outline" href="/api/journal/vouchers/{id}/pdf/" download><FileDown />PDF</Button>
			{#if v.status === 'ACTIVE' && auth.can('journal.cancel')}<Button variant="destructive" onclick={() => (cancelOpen = true)}><Ban />Cancel</Button>{/if}
		{/snippet}
	</PageHeader>

	{#if v.status === 'CANCELLED'}
		<p class="mb-4 rounded-md border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-800">Cancelled {dateTime(v.cancelled_at)} by {v.cancelled_by_name}: {v.cancel_reason}. It no longer counts in any ledger.</p>
	{/if}

	<Card title="Entries" bodyClass="p-0">
		<div class="overflow-x-auto">
			<table class="table-base">
				<thead><tr><th>#</th><th>Account</th><th>Type</th><th>Line note</th><th class="num">Debit</th><th class="num">Credit</th></tr></thead>
				<tbody>
					{#each v.lines as l (l.line_no)}
						{@const href = accountHref(l.account)}
						<tr>
							<td class="text-muted-foreground">{l.line_no}</td>
							<td class="font-medium">{#if href}<a class="text-primary hover:underline" {href}>{l.account.name}</a>{:else}{l.account.name}{/if}</td>
							<td class="text-muted-foreground">{l.account.group}</td>
							<td>{l.narration}</td>
							<td class="num">{money(l.debit, true)}</td>
							<td class="num">{money(l.credit, true)}</td>
						</tr>
					{/each}
				</tbody>
				<tfoot>
					<tr class="bg-muted/40 font-semibold"><td></td><td colspan="3">Total ({auth.currency})</td><td class="num">{money(v.total_amount)}</td><td class="num">{money(v.total_amount)}</td></tr>
				</tfoot>
			</table>
		</div>
	</Card>

	<Card title="Details" class="mt-4">
		<dl class="grid gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
			<div class="sm:col-span-2"><dt class="text-xs text-muted-foreground">Narration</dt><dd class="whitespace-pre-line">{v.narration || '—'}</dd></div>
			<div><dt class="text-xs text-muted-foreground">Recorded by</dt><dd>{v.created_by_name} · {dateTime(v.created_at)}</dd></div>
		</dl>
	</Card>

	<ConfirmDialog bind:open={cancelOpen} title="Cancel {v.voucher_number}?" requireReason destructive confirmLabel="Cancel voucher"
		message="The voucher is kept for audit but no longer counts in any ledger, cash book or party balance." onConfirm={cancel} />
{:else if q.isError}
	<h1 class="text-xl font-semibold">Not available</h1>
	<p class="mt-1 text-destructive">{q.error.message}</p>
{/if}
