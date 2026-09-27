<script lang="ts">
	import { api, fieldErrors, ApiError } from '$lib/api/client';
	import { toast } from '$lib/stores/toast.svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { UNITS } from '$lib/utilities/format';
	import type { Product } from '$lib/types';
	import Dialog from '$lib/components/ui/dialog.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Button from '$lib/components/ui/button.svelte';

	let {
		open = $bindable(false),
		product = null,
		onSaved
	}: { open?: boolean; product?: Product | null; onSaved?: (p: Product) => void } = $props();

	const blank = () => ({
		name: '',
		sku_code: '',
		unit: 'PCS',
		description: '',
		opening_stock: '0',
		reorder_level: '0',
		purchase_price: '0',
		selling_price: '0',
		tax_rate: auth.settings?.default_tax_rate ?? '13.00'
	});
	let form = $state(blank());
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);

	$effect(() => {
		if (open) {
			errors = {};
			form = product
				? {
						name: product.name,
						sku_code: product.sku_code,
						unit: product.unit,
						description: product.description,
						opening_stock: product.opening_stock,
						reorder_level: product.reorder_level,
						purchase_price: product.purchase_price,
						selling_price: product.selling_price,
						tax_rate: product.tax_rate
					}
				: blank();
		}
	});

	async function save(e: SubmitEvent) {
		e.preventDefault();
		busy = true;
		errors = {};
		try {
			const body = { ...form } as Record<string, string>;
			if (product) delete body.opening_stock;
			const res = product ? await api.patch<Product>(`products/${product.id}/`, body) : await api.post<Product>('products/', body);
			toast.success(res.message);
			open = false;
			onSaved?.(res.data);
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError && !Object.keys(errors).length) toast.error(err.message);
		} finally {
			busy = false;
		}
	}
</script>

<Dialog bind:open title={product ? `Edit ${product.name}` : 'New product'} description="Prices are in {auth.currency}. Tax is applied per product." class="max-w-2xl">
	<form id="product-form" class="grid gap-4 sm:grid-cols-2" onsubmit={save}>
		<Field label="Name" for="p-name" required error={errors.name} class="sm:col-span-2">
			<Input id="p-name" bind:value={form.name} required maxlength={200} aria-invalid={!!errors.name} />
		</Field>
		<Field label="SKU code" for="p-sku" required error={errors.sku_code} hint="Unique; stored in upper case.">
			<Input id="p-sku" bind:value={form.sku_code} required maxlength={64} aria-invalid={!!errors.sku_code} />
		</Field>
		<Field label="Unit" for="p-unit" error={errors.unit}>
			<Select id="p-unit" bind:value={form.unit} options={UNITS} />
		</Field>
		<Field label="Purchase price" for="p-cost" error={errors.purchase_price}>
			<Input id="p-cost" type="number" step="0.01" min="0" bind:value={form.purchase_price} class="text-right" />
		</Field>
		<Field label="Selling price" for="p-price" error={errors.selling_price}>
			<Input id="p-price" type="number" step="0.01" min="0" bind:value={form.selling_price} class="text-right" />
		</Field>
		<Field label="Tax rate (%)" for="p-tax" error={errors.tax_rate}>
			<Input id="p-tax" type="number" step="0.01" min="0" max="100" bind:value={form.tax_rate} class="text-right" />
		</Field>
		<Field label="Reorder level" for="p-reorder" error={errors.reorder_level}>
			<Input id="p-reorder" type="number" step="0.001" min="0" bind:value={form.reorder_level} class="text-right" />
		</Field>
		<Field
			label="Opening stock"
			for="p-opening"
			error={errors.opening_stock}
			hint={product ? 'Posted to the stock ledger; use a stock adjustment to correct.' : 'Posted to the stock ledger on save.'}
		>
			<Input id="p-opening" type="number" step="0.001" min="0" bind:value={form.opening_stock} disabled={!!product} class="text-right" />
		</Field>
		<Field label="Description" for="p-desc" class="sm:col-span-2">
			<Textarea id="p-desc" bind:value={form.description} rows={2} />
		</Field>
	</form>
	{#snippet footer()}
		<Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
		<Button type="submit" form="product-form" loading={busy}>{product ? 'Save changes' : 'Create product'}</Button>
	{/snippet}
</Dialog>
