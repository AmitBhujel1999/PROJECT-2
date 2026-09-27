<script lang="ts">
	import { page } from '$app/state';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { Printer, FileDown, Ban, HandCoins } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { money, qty, date as fmtDate, dateTime } from '$lib/utilities/format';
	import type { TradeDoc } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';

	let { kind, id }: { kind: 'sale' | 'purchase'; id: number } = $props();
	// svelte-ignore state_referenced_locally
	const isSale = kind === 'sale';
	const path = isSale ? 'sales' : 'purchases';
	const client = useQueryClient();
	const q = createQuery(() => ({ queryKey: [path, id], queryFn: () => api.get<TradeDoc>(`${path}/${id}/`) }));
	const d = $derived(q.data);
	const number = $derived(d ? (isSale ? d.invoice_number : d.bill_number) : '');
	const pdfUrl = $derived(`/api/${path}/${id}/pdf/`);

	let cancelOpen = $state(false);
	let printFrame: HTMLIFrameElement | null = $state(null);
	let printing = $state(false);

	function printPdf() {
		printing = true;
		const frame = printFrame!;
		frame.onload = () => {
			try {
				frame.contentWindow?.focus();
				frame.contentWindow?.print();
			} catch {
				window.open(`${pdfUrl}?inline=1`, '_blank', 'noopener');
			}
			printing = false;
		};
		frame.src = `${pdfUrl}?inline=1&t=${Date.now()}`;
	}

	function downloadPdf() {
		const a = document.createElement('a');
		a.href = pdfUrl;
		a.download = `${number}.pdf`;
		a.click();
	}

	let handled = false;
	$effect(() => {
		if (!d || handled) return;
		handled = true;
		if (page.url.searchParams.get('print') === '1') printPdf();
		if (page.url.searchParams.get('pdf') === '1') downloadPdf();
	});

	async function cancel(reason: string) {
		const res = await api.post<TradeDoc>(`${path}/${id}/cancel/`, { reason });
		toast.success(res.message);
		client.setQueryData([path, id], res.data);
	}
</script>

{#if d}
	<PageHeader title="{isSale ? 'Invoice' : 'Bill'} {number}" description="{isSale ? d.customer_name : d.vendor_name} · {fmtDate(d.date)}" back={{ href: `/${path}`, label: isSale ? 'Sales' : 'Purchases' }}>
		{#snippet actions()}
			<StatusBadge status={d.payment_status} />
			<StatusBadge status={d.status} />
			<Button variant="outline" onclick={printPdf} loading={printing} data-testid="print-doc"><Printer />Print</Button>
			<Button variant="outline" href={pdfUrl} download data-testid="download-pdf"><FileDown />PDF</Button>
			{#if d.status === 'ACTIVE' && Number(d.balance_due) > 0 && auth.can(isSale ? 'receipts.create' : 'payments.create')}
				<Button variant="outline" href="/{isSale ? 'receipts' : 'payments'}/new?party={isSale ? d.customer : d.vendor}&document={d.id}"><HandCoins />Record {isSale ? 'receipt' : 'payment'}</Button>
			{/if}
			{#if d.status === 'ACTIVE' && auth.can(isSale ? 'sales.cancel' : 'purchases.cancel')}
				<Button variant="destructive" onclick={() => (cancelOpen = true)}><Ban />Cancel</Button>
			{/if}
		{/snippet}
	</PageHeader>

	{#if d.status === 'CANCELLED'}
		<p class="mb-4 rounded-md border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-800">
			Cancelled {dateTime(d.cancelled_at)} — {d.cancel_reason}. Stock was restored by reversing ledger entries; the original record is preserved.
		</p>
	{/if}

	<div class="grid gap-4 xl:grid-cols-[1fr_22rem]">
		<Card bodyClass="p-0">
			<div class="grid gap-4 border-b p-4 sm:grid-cols-3">
				<div>
					<p class="text-xs uppercase text-muted-foreground">{isSale ? 'Bill to' : 'Vendor'}</p>
					<a class="font-semibold text-primary hover:underline" href="/{isSale ? 'customers' : 'vendors'}/{isSale ? d.customer : d.vendor}">{isSale ? d.customer_name : d.vendor_name}</a>
					{#if d[isSale ? 'customer_pan_vat_no' : 'vendor_pan_vat_no']}<p class="text-sm text-muted-foreground">PAN/VAT {d[isSale ? 'customer_pan_vat_no' : 'vendor_pan_vat_no']}</p>{/if}
					<p class="text-sm text-muted-foreground">{d[isSale ? 'customer_address' : 'vendor_address']}</p>
				</div>
				<div class="text-sm">
					<p><span class="text-muted-foreground">Date:</span> {fmtDate(d.date)}</p>
					<p><span class="text-muted-foreground">Due:</span> {fmtDate(d.due_date)}</p>
					{#if !isSale && d.vendor_bill_number}<p><span class="text-muted-foreground">Vendor bill:</span> {d.vendor_bill_number}</p>{/if}
				</div>
				<div class="text-sm">
					<p><span class="text-muted-foreground">Created by:</span> {d.created_by_name}</p>
					{#if d.notes}<p class="text-muted-foreground">{d.notes}</p>{/if}
				</div>
			</div>
			<div class="overflow-x-auto">
				<table class="table-base" data-testid="doc-items">
					<thead>
						<tr><th>#</th><th>Item</th><th class="num">Qty</th><th class="num">{isSale ? 'Price' : 'Cost'}</th><th class="num">Gross</th><th class="num">Discount</th><th class="num">Taxable</th><th class="num">{auth.taxLabel}</th><th class="num">Total</th></tr>
					</thead>
					<tbody>
						{#each d.items ?? [] as it, i (it.id)}
							<tr>
								<td>{i + 1}</td>
								<td><a class="hover:underline" href="/products/{it.product}">{it.product_name}</a> <span class="text-xs text-muted-foreground">{it.sku_code}</span></td>
								<td class="num">{qty(it.quantity)} {it.unit}</td>
								<td class="num">{money(isSale ? it.unit_price : it.unit_cost)}</td>
								<td class="num">{money(it.gross_amount)}</td>
								<td class="num">
									{money(it.discount_amount, true)}{#if it.discount_type === 'PERCENTAGE'} <span class="text-[11px] text-muted-foreground">({it.discount_value}%)</span>{/if}
									{#if Number(it.invoice_discount_share) > 0}<div class="text-[11px] text-muted-foreground">+ {money(it.invoice_discount_share)} inv.</div>{/if}
								</td>
								<td class="num">{money(it.taxable_amount)}</td>
								<td class="num">{money(it.tax_amount)} <span class="text-[11px] text-muted-foreground">({it.tax_rate}%)</span></td>
								<td class="num font-medium">{money(isSale ? it.total_price : it.total_cost)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</Card>

		<div class="grid content-start gap-4">
			<Card title="Totals">
				<dl class="grid grid-cols-2 gap-y-1.5 text-sm" data-testid="doc-totals">
					<dt class="text-muted-foreground">Subtotal</dt><dd class="num" data-testid="d-subtotal">{money(d.subtotal)}</dd>
					<dt class="text-muted-foreground">Item discounts</dt><dd class="num" data-testid="d-item-discount">{money(d.item_discount_total)}</dd>
					<dt class="text-muted-foreground">Invoice discount{#if d.discount_type === 'PERCENTAGE'} ({d.discount_value}%){/if}</dt><dd class="num" data-testid="d-invoice-discount">{money(d.discount_amount)}</dd>
					<dt class="text-muted-foreground">Taxable amount</dt><dd class="num" data-testid="d-taxable">{money(d.taxable_amount)}</dd>
					<dt class="text-muted-foreground">{auth.taxLabel}</dt><dd class="num" data-testid="d-tax">{money(d.tax_amount)}</dd>
					<dt class="border-t pt-2 font-semibold">Grand total</dt><dd class="num border-t pt-2 text-lg font-bold" data-testid="d-total">{money(d.total_amount)}</dd>
					<dt class="text-muted-foreground">Paid</dt><dd class="num">{money(d.amount_paid)}</dd>
					<dt class="font-medium">Balance due</dt><dd class="num font-semibold" data-testid="d-balance">{money(d.balance_due)}</dd>
				</dl>
			</Card>
			{#if d.allocations?.length}
				<Card title={isSale ? 'Receipts applied' : 'Payments applied'} bodyClass="p-0">
					<table class="table-base">
						<tbody>
							{#each d.allocations as a (a.id)}
								<tr class:line-through={!a.is_active} class:opacity-50={!a.is_active}>
									<td><a class="text-primary hover:underline" href="/{isSale ? 'receipts' : 'payments'}/{isSale ? a.receipt_id : a.payment_id}">{isSale ? a.receipt_number : a.payment_number}</a></td>
									<td>{fmtDate(a.date)}</td>
									<td class="num">{money(a.amount)}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</Card>
			{/if}
		</div>
	</div>

	<iframe bind:this={printFrame} title="Print preview" class="hidden" aria-hidden="true"></iframe>
	<ConfirmDialog bind:open={cancelOpen} title="Cancel {number}?" requireReason reasonLabel="Cancellation reason" destructive confirmLabel="Cancel document"
		message="The document is kept for audit, stock is reversed with new ledger entries and any payments applied become unallocated advances." onConfirm={cancel} />
{:else if q.isError}
	<p class="text-destructive">{q.error.message}</p>
{/if}
