<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { Plus, Trash2, Save, Printer, FileDown, TriangleAlert } from '@lucide/svelte';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { debounce } from '$lib/utilities/debounce';
	import { money, qty as fmtQty, today, addDays, PAYMENT_METHODS } from '$lib/utilities/format';
	import type { Paginated, Party, Product, Totals, TradeDoc } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Combobox from '$lib/components/ui/combobox.svelte';
	import Spinner from '$lib/components/ui/spinner.svelte';
	import PartyPicker from '$lib/components/parties/party-picker.svelte';
	import { phone } from '$lib/stores/viewport.svelte';

	/**
	 * Sales invoice / purchase bill entry. The browser only collects inputs;
	 * every amount shown is calculated by Django (/calculate/ preview) and
	 * recalculated again on save. Nothing here is authoritative.
	 */
	let { kind }: { kind: 'sale' | 'purchase' } = $props();

	const isSale = $derived(kind === 'sale');
	const apiPath = $derived(isSale ? 'sales' : 'purchases');
	const partyType = $derived(isSale ? 'CUSTOMER' : 'VENDOR');
	const canOverrideDue = $derived(auth.can('documents.override_due_date'));

	interface Row {
		key: number;
		product: number | null;
		label: string;
		unit: string;
		stock: string | null;
		quantity: string;
		unit_price: string;
		discount_type: '' | 'PERCENTAGE' | 'FIXED';
		discount_value: string;
	}

	let nextKey = 1;
	const newRow = (): Row => ({ key: nextKey++, product: null, label: '', unit: '', stock: null, quantity: '1', unit_price: '', discount_type: '', discount_value: '' });

	let partyId = $state<number | null>(null);
	let partyLabel = $state('');
	let creditDays = $state(0);
	let date = $state(today());
	let dueDate = $state(today());
	let dueTouched = $state(false);
	let vendorBillNumber = $state('');
	let rows = $state<Row[]>([newRow()]);
	let invDiscountType = $state<'' | 'PERCENTAGE' | 'FIXED'>('');
	let invDiscountValue = $state('');
	let notes = $state('');
	let paymentStatus = $state<'UNPAID' | 'PAID' | 'PARTIAL'>('UNPAID');
	let paidAmount = $state('');
	let paymentMethod = $state('CASH');
	let paymentRef = $state('');

	let totals = $state<Totals | null>(null);
	let previewError = $state('');
	let previewing = $state(false);
	let saving = $state(false);
	let errors = $state<Record<string, string>>({});
	let stockErrorProduct = $state<number | null>(null);

	// Preselect a party from ?party=
	$effect(() => {
		const pid = page.url.searchParams.get('party');
		if (pid && !partyId) {
			api.get<Party>(`${isSale ? 'customers' : 'vendors'}/${pid}/`).then(selectParty).catch(() => {});
		}
	});

	function selectParty(p: Party | null) {
		partyId = p?.id ?? null;
		partyLabel = p?.name ?? '';
		creditDays = p?.credit_days ?? 0;
		if (!dueTouched || !canOverrideDue) dueDate = addDays(date, creditDays);
		schedulePreview();
	}

	function onDateChange() {
		if (!dueTouched || !canOverrideDue) dueDate = addDays(date, creditDays);
		schedulePreview();
	}

	async function loadProducts(q: string) {
		const data = await api.get<Paginated<Product>>('products/', { search: q, is_active: 'true', page_size: 25 });
		return data.results;
	}

	function selectProduct(row: Row, p: Product | null) {
		if (!p) {
			row.product = null;
			row.stock = null;
		} else {
			row.product = p.id;
			row.unit = p.unit_display;
			row.stock = p.current_stock;
			row.unit_price = isSale ? p.selling_price : p.purchase_price;
			if (rows[rows.length - 1] === row) rows.push(newRow());
		}
		schedulePreview();
	}

	function removeRow(i: number) {
		rows.splice(i, 1);
		if (!rows.length) rows.push(newRow());
		schedulePreview();
	}

	const validRows = $derived(rows.filter((r) => r.product && Number(r.quantity) > 0));

	function payload(): Record<string, unknown> {
		const body: Record<string, unknown> = {
			party: partyId,
			date,
			discount_type: invDiscountType || null,
			discount_value: invDiscountType ? invDiscountValue || '0' : '0',
			notes,
			items: validRows.map((r) => ({
				product: r.product,
				quantity: r.quantity,
				unit_price: r.unit_price === '' ? null : r.unit_price,
				discount_type: r.discount_type || null,
				discount_value: r.discount_type ? r.discount_value || '0' : '0'
			}))
		};
		if (dueDate) body.due_date = dueDate;
		if (!isSale) body.vendor_bill_number = vendorBillNumber;
		if (paymentStatus === 'PAID') body.payment = { pay_in_full: true, payment_method: paymentMethod, reference_number: paymentRef };
		if (paymentStatus === 'PARTIAL') body.payment = { amount: paidAmount, payment_method: paymentMethod, reference_number: paymentRef };
		return body;
	}

	async function preview() {
		if (!validRows.length) {
			totals = null;
			previewError = '';
			return;
		}
		previewing = true;
		try {
			const body = payload();
			delete body.payment;
			delete body.due_date;
			if (!body.party) delete body.party;
			const res = await api.post<Totals>(`${apiPath}/calculate/`, body);
			totals = res.data;
			previewError = '';
		} catch (err) {
			previewError = err instanceof ApiError ? err.message : 'Could not calculate totals.';
		} finally {
			previewing = false;
		}
	}
	const schedulePreview = debounce(preview, 300);

	function lineFor(row: Row) {
		if (!totals || !row.product) return null;
		const idx = validRows.indexOf(row);
		return idx >= 0 ? totals.lines[idx] : null;
	}

	async function save(action: 'save' | 'print' | 'pdf') {
		errors = {};
		stockErrorProduct = null;
		if (!partyId) {
			errors.party = `Select a ${isSale ? 'customer' : 'vendor'}.`;
			return;
		}
		if (!validRows.length) {
			errors.items = 'Add at least one item with a quantity.';
			return;
		}
		saving = true;
		try {
			const res = await api.post<TradeDoc>(`${apiPath}/`, payload());
			const doc = res.data;
			toast.success(`✓ ${res.message}`);
			for (const w of doc.warnings ?? []) toast.warning(`⚠ ${w}`);
			await goto(`/${apiPath}/${doc.id}${action === 'print' ? '?print=1' : action === 'pdf' ? '?pdf=1' : ''}`);
		} catch (err) {
			if (err instanceof ApiError) {
				errors = fieldErrors(err);
				if (err.code === 'INSUFFICIENT_STOCK') {
					const d = err.details as { product_id: number };
					stockErrorProduct = d?.product_id ?? null;
					toast.error(`✕ ${err.message} Sale cannot be completed.`);
				} else {
					toast.error(`✕ ${err.message}`);
				}
			} else toast.error('✕ Save failed.');
		} finally {
			saving = false;
		}
	}

	function addRow() {
		rows.push(newRow());
		setTimeout(() => document.getElementById(`item-${rows[rows.length - 1].key}`)?.focus());
	}

	function onKeydown(e: KeyboardEvent) {
		if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
			e.preventDefault();
			save('save');
		} else if (e.altKey && e.key.toLowerCase() === 'n') {
			e.preventDefault();
			addRow();
		}
	}

	function step(row: Row, delta: number) {
		const next = Math.max(1, Math.floor(Number(row.quantity) || 0) + delta);
		row.quantity = String(next);
		schedulePreview();
	}

	const paidPreview = $derived(paymentStatus === 'PAID' ? totals?.total_amount : paymentStatus === 'PARTIAL' ? paidAmount : '0');
</script>

<svelte:window onkeydown={onKeydown} />

<PageHeader
	title={isSale ? 'New sale' : 'New purchase'}
	description="Invoice number is assigned when saved. Shortcuts: Ctrl+S save · Alt+N add item."
	back={{ href: `/${apiPath}`, label: isSale ? 'Sales' : 'Purchases' }}
/>

<div class="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_22rem] [&>*]:min-w-0">
	<div class="grid gap-4 [&>*]:min-w-0">
		<Card>
			<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
				<Field label={isSale ? 'Customer' : 'Vendor'} for="party" required error={errors.party} class="sm:col-span-2">
					<PartyPicker type={partyType} id="party" bind:value={partyId} bind:label={partyLabel} invalid={!!errors.party} onSelect={selectParty} />
				</Field>
				<Field label={isSale ? 'Invoice no.' : 'Bill no.'} for="doc-no">
					<Input id="doc-no" value={isSale ? 'INV-auto' : 'BILL-auto'} disabled />
				</Field>
				{#if !isSale}
					<Field label="Vendor's bill no." for="vendor-bill" error={errors.vendor_bill_number}>
						<Input id="vendor-bill" bind:value={vendorBillNumber} maxlength={60} />
					</Field>
				{/if}
				<Field label="Date" for="doc-date" required error={errors.date}>
					<Input id="doc-date" type="date" bind:value={date} onchange={onDateChange} required />
				</Field>
				<Field label="Due date" for="due-date" error={errors.due_date} hint={canOverrideDue ? `Default: ${creditDays} day credit terms` : 'From credit terms'}>
					<Input id="due-date" type="date" bind:value={dueDate} min={date} disabled={!canOverrideDue} onchange={() => (dueTouched = true)} />
				</Field>
			</div>
		</Card>

		<Card title="Items" bodyClass="p-0">
			{#snippet actions()}
				<div class="flex items-center gap-2">
					{#if previewing}<Spinner class="text-muted-foreground" />{/if}
					<Button size="sm" onclick={addRow}><Plus />Add item</Button>
				</div>
			{/snippet}
			{#if phone.current}
				<div class="grid gap-2 bg-muted/50 p-2" data-testid="entry-items">
					{#each rows as row, i (row.key)}
						{@const ln = lineFor(row)}
						{@const stock = ln?.available_stock ?? row.stock}
						{@const short = isSale && row.product && stock !== null && Number(row.quantity) > Number(stock)}
						<div class="rounded-xl border bg-background p-2.5 shadow-xs" class:border-red-400={stockErrorProduct === row.product && row.product !== null}>
							<div class="flex items-start gap-2">
								<div class="min-w-0 flex-1">
									<Combobox id="item-{row.key}" bind:value={row.product} bind:label={row.label} load={loadProducts}
										getLabel={(p: Product) => `${p.name} (${p.sku_code})`} placeholder="Search item or SKU…" onSelect={(p) => selectProduct(row, p)}>
										{#snippet item(p: Product)}
											<div class="grid">
												<span class="font-medium">{p.name}</span>
												<span class="text-xs tabular-nums text-muted-foreground">{p.sku_code} · Stock {fmtQty(p.current_stock)} · {money(isSale ? p.selling_price : p.purchase_price)}</span>
											</div>
										{/snippet}
									</Combobox>
								</div>
								<button type="button" class="rounded p-2 text-muted-foreground" onclick={() => removeRow(i)} aria-label="Remove row {i + 1}"><Trash2 class="size-4" /></button>
							</div>
							{#if row.product}
								<div class="mt-2 grid grid-cols-2 gap-2">
									<div>
										<p class="mb-1 text-[11px] text-muted-foreground">Qty {#if stock !== null}<span class:text-red-600={short}>· {fmtQty(stock)} {row.unit} in stock</span>{/if}</p>
										<div class="flex h-9 overflow-hidden rounded-md border">
											<button type="button" class="w-9 bg-muted font-bold" onclick={() => step(row, -1)} aria-label="Less">−</button>
											<input type="number" min="0.001" step="any" bind:value={row.quantity} oninput={schedulePreview} class="w-full min-w-0 text-center text-sm font-semibold outline-none" aria-label="Quantity row {i + 1}" aria-invalid={!!short} />
											<button type="button" class="w-9 bg-muted font-bold" onclick={() => step(row, 1)} aria-label="More">+</button>
										</div>
									</div>
									<div>
										<p class="mb-1 text-[11px] text-muted-foreground">{isSale ? 'Price' : 'Unit cost'}</p>
										<Input type="number" min="0" step="0.01" bind:value={row.unit_price} oninput={schedulePreview} class="h-9 text-right" aria-label="Price row {i + 1}" />
									</div>
									<div class="col-span-2 flex items-center gap-2">
										<select class="h-9 w-24 rounded-md border bg-background px-1.5 text-sm" bind:value={row.discount_type} onchange={schedulePreview} aria-label="Discount type row {i + 1}">
											<option value="">No disc.</option><option value="PERCENTAGE">%</option><option value="FIXED">Fixed</option>
										</select>
										{#if row.discount_type}
											<Input type="number" min="0" step="0.01" bind:value={row.discount_value} oninput={schedulePreview} class="h-9 w-24 text-right" aria-label="Discount value row {i + 1}" />
										{/if}
										<div class="ml-auto text-right">
											<p class="font-semibold tabular-nums">{ln ? money(ln.net_amount) : '—'}</p>
											{#if ln}<p class="text-[11px] text-muted-foreground">{auth.taxLabel} {ln.tax_rate}%{Number(ln.discount_amount) > 0 ? ` · −${money(ln.discount_amount)}` : ''}</p>{/if}
										</div>
									</div>
								</div>
							{/if}
						</div>
					{/each}
				</div>
			{:else}
			<div class="overflow-x-auto">
				<table class="table-base min-w-[980px]" data-testid="entry-items">
					<thead>
						<tr>
							<th class="w-8">#</th>
							<th class="min-w-64">Item</th>
							<th class="num">Stock</th>
							<th class="num w-24">Qty</th>
							<th class="num w-32">{isSale ? 'Price' : 'Unit cost'}</th>
							<th class="w-28">Discount</th>
							<th class="num w-24">Value</th>
							<th class="num">Tax %</th>
							<th class="num">Amount</th>
							<th class="w-10" aria-label="Remove"></th>
						</tr>
					</thead>
					<tbody>
						{#each rows as row, i (row.key)}
							{@const ln = lineFor(row)}
							{@const stock = ln?.available_stock ?? row.stock}
							{@const short = isSale && row.product && stock !== null && Number(row.quantity) > Number(stock)}
							<tr class:bg-red-50={stockErrorProduct === row.product && row.product !== null}>
								<td class="text-muted-foreground">{i + 1}</td>
								<td>
									<Combobox id="item-{row.key}" bind:value={row.product} bind:label={row.label} load={loadProducts}
										getLabel={(p: Product) => `${p.name} (${p.sku_code})`} placeholder="Search item or SKU…" onSelect={(p) => selectProduct(row, p)}>
										{#snippet item(p: Product)}
											<div class="flex items-center justify-between gap-3">
												<span><span class="font-medium">{p.name}</span> <span class="text-xs text-muted-foreground">{p.sku_code}</span></span>
												<span class="text-xs tabular-nums text-muted-foreground">Stock {fmtQty(p.current_stock)} {p.unit_display} · {money(isSale ? p.selling_price : p.purchase_price)}</span>
											</div>
										{/snippet}
									</Combobox>
								</td>
								<td class="num" class:text-red-600={short}>
									{#if row.product}{fmtQty(stock)} <span class="text-xs text-muted-foreground">{row.unit}</span>{/if}
								</td>
								<td>
									<Input type="number" min="0.001" step="any" bind:value={row.quantity} oninput={schedulePreview} class="h-8 w-24 text-right" aria-label="Quantity row {i + 1}" aria-invalid={!!short} />
								</td>
								<td><Input type="number" min="0" step="0.01" bind:value={row.unit_price} oninput={schedulePreview} class="h-8 w-32 text-right" aria-label="Price row {i + 1}" /></td>
								<td>
									<select class="h-8 w-24 rounded-md border bg-background px-1.5 text-sm" bind:value={row.discount_type} onchange={schedulePreview} aria-label="Discount type row {i + 1}">
										<option value="">None</option><option value="PERCENTAGE">%</option><option value="FIXED">Fixed</option>
									</select>
								</td>
								<td><Input type="number" min="0" step="0.01" bind:value={row.discount_value} oninput={schedulePreview} disabled={!row.discount_type} class="h-8 w-24 text-right" aria-label="Discount value row {i + 1}" /></td>
								<td class="num text-muted-foreground">{ln?.tax_rate ?? ''}</td>
								<td class="num font-medium">
									{#if ln}
										{money(ln.net_amount)}
										{#if Number(ln.discount_amount) > 0}<div class="text-[11px] font-normal text-muted-foreground">−{money(ln.discount_amount)}</div>{/if}
									{/if}
								</td>
								<td>
									<button type="button" class="rounded p-1.5 text-muted-foreground hover:bg-red-50 hover:text-red-600" onclick={() => removeRow(i)} aria-label="Remove row {i + 1}"><Trash2 class="size-4" /></button>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{/if}
			<div class="grid gap-2 border-t p-3">
				<button type="button" onclick={addRow} data-testid="add-item"
					class="flex h-11 w-full items-center justify-center gap-2 rounded-md border-2 border-dashed border-primary/40 text-sm font-semibold text-primary hover:border-primary hover:bg-primary/5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/60">
					<Plus class="size-4" />Add item <span class="font-normal text-muted-foreground">(Alt+N)</span>
				</button>
				{#if errors.items}<p class="text-sm text-destructive">{errors.items}</p>{/if}
			</div>
		</Card>

		<Card>
			<Field label="Notes" for="notes"><Textarea id="notes" bind:value={notes} rows={2} maxlength={2000} /></Field>
		</Card>
	</div>

	<div class="grid content-start gap-4">
		<Card title="Summary">
			<div class="mb-3 grid grid-cols-[1fr_7rem] gap-2">
				<Field label="Invoice discount" for="inv-dtype">
					<Select id="inv-dtype" bind:value={invDiscountType} onchange={schedulePreview}
						options={[{ value: 'PERCENTAGE', label: 'Percentage (%)' }, { value: 'FIXED', label: `Fixed (${auth.currency})` }]} placeholder="No discount" />
				</Field>
				<Field label="Value" for="inv-dval">
					<Input id="inv-dval" type="number" min="0" step="0.01" bind:value={invDiscountValue} oninput={schedulePreview} disabled={!invDiscountType} class="text-right" />
				</Field>
			</div>
			<dl class="grid grid-cols-2 gap-y-1.5 text-sm" data-testid="entry-totals">
				<dt class="text-muted-foreground">Subtotal</dt><dd class="num" data-testid="t-subtotal">{money(totals?.subtotal ?? '0')}</dd>
				<dt class="text-muted-foreground">Item discounts</dt><dd class="num" data-testid="t-item-discount">{money(totals?.item_discount_total ?? '0')}</dd>
				<dt class="text-muted-foreground">Invoice discount</dt><dd class="num" data-testid="t-invoice-discount">{money(totals?.discount_amount ?? '0')}</dd>
				<dt class="text-muted-foreground">Taxable amount</dt><dd class="num" data-testid="t-taxable">{money(totals?.taxable_amount ?? '0')}</dd>
				<dt class="text-muted-foreground">{auth.taxLabel}</dt><dd class="num" data-testid="t-tax">{money(totals?.tax_amount ?? '0')}</dd>
				<dt class="mt-1 border-t pt-2 font-semibold">GRAND TOTAL</dt>
				<dd class="num mt-1 border-t pt-2 text-lg font-bold" data-testid="t-total">{auth.currency} {money(totals?.total_amount ?? '0')}</dd>
			</dl>
			{#if previewError}<p class="mt-2 flex gap-1.5 text-xs text-destructive" role="alert"><TriangleAlert class="size-4 shrink-0" />{previewError}</p>{/if}
			<p class="mt-2 text-[11px] text-muted-foreground">Calculated by the server; recalculated again when saved.</p>
		</Card>

		<Card title="Payment">
			<div class="grid gap-3">
				<Field label="Payment status" for="pay-status">
					<Select id="pay-status" bind:value={paymentStatus}
						options={[{ value: 'UNPAID', label: 'Unpaid (on credit)' }, { value: 'PAID', label: 'Paid in full' }, { value: 'PARTIAL', label: 'Partially paid' }]} />
				</Field>
				{#if paymentStatus !== 'UNPAID'}
					{#if paymentStatus === 'PARTIAL'}
						<Field label="Amount paid" for="pay-amount" error={errors.payment}>
							<Input id="pay-amount" type="number" min="0.01" step="0.01" bind:value={paidAmount} class="text-right" />
						</Field>
					{/if}
					<Field label="Method" for="pay-method"><Select id="pay-method" bind:value={paymentMethod} options={PAYMENT_METHODS} /></Field>
					<Field label="Reference" for="pay-ref"><Input id="pay-ref" bind:value={paymentRef} maxlength={60} placeholder="Cheque / txn no." /></Field>
					<p class="text-xs text-muted-foreground">A {isSale ? 'receipt' : 'payment'} of {money(paidPreview)} will be recorded and allocated to this {isSale ? 'invoice' : 'bill'}.</p>
				{/if}
			</div>
		</Card>

		{#if phone.current}
			<!-- Phone: total and save stay at the bottom of the screen -->
			<div class="h-20"></div>
			<div class="no-print fixed inset-x-0 bottom-0 z-40 flex items-center gap-3 border-t bg-background px-3 pt-2 shadow-[0_-4px_12px_rgba(0,0,0,0.06)]" style="padding-bottom: calc(10px + env(safe-area-inset-bottom))">
				<div class="min-w-0 flex-1">
					<p class="text-[11px] text-muted-foreground">Total ({auth.currency}){#if previewing} · updating…{/if}</p>
					<p class="text-lg font-bold tabular-nums">{money(totals?.total_amount ?? '0')}</p>
				</div>
				<Button size="lg" loading={saving} onclick={() => save('save')} data-testid="save-doc"><Save />Save {isSale ? 'sale' : 'purchase'}</Button>
			</div>
		{:else}
		<div class="grid gap-2">
			<Button size="lg" loading={saving} onclick={() => save('save')} data-testid="save-doc"><Save />Save {isSale ? 'sale' : 'purchase'}</Button>
			<div class="grid grid-cols-2 gap-2">
				<Button variant="outline" disabled={saving} onclick={() => save('print')} data-testid="save-print"><Printer />Save & Print</Button>
				<Button variant="outline" disabled={saving} onclick={() => save('pdf')} data-testid="save-pdf"><FileDown />Save & PDF</Button>
			</div>
		</div>
		{/if}
	</div>
</div>
