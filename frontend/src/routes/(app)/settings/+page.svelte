<script lang="ts">
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import type { BusinessSettings } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Button from '$lib/components/ui/button.svelte';

	let form = $state<BusinessSettings | null>(null);
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);
	const readOnly = $derived(!auth.can('settings.manage'));

	api.get<BusinessSettings>('settings/').then((s) => (form = s));

	async function save(e: SubmitEvent) {
		e.preventDefault();
		busy = true;
		errors = {};
		try {
			const res = await api.put<BusinessSettings>('settings/', form);
			form = res.data;
			auth.settings = res.data;
			toast.success(res.message);
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError) toast.error(err.message);
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Settings · Accounting</title></svelte:head>
<PageHeader title="Business settings" description="Shown on invoices, statements and reports. Tax rates are configurable — 13% VAT is only the default for new products." />

{#if form}
	<form onsubmit={save}>
		<Card>
			<fieldset disabled={readOnly} class="grid gap-4 sm:grid-cols-2">
				<Field label="Business name" for="s-name" required error={errors.business_name} class="sm:col-span-2"><Input id="s-name" bind:value={form.business_name} required /></Field>
				<Field label="PAN/VAT No." for="s-pan" error={errors.pan_vat_no}><Input id="s-pan" bind:value={form.pan_vat_no} /></Field>
				<Field label="Phone" for="s-phone"><Input id="s-phone" bind:value={form.phone} /></Field>
				<Field label="Email" for="s-email" error={errors.email}><Input id="s-email" type="email" bind:value={form.email} /></Field>
				<Field label="Address" for="s-address" class="sm:col-span-2"><Textarea id="s-address" bind:value={form.address} rows={2} /></Field>
				<Field label="Currency code" for="s-cur" error={errors.currency_code}><Input id="s-cur" bind:value={form.currency_code} maxlength={3} /></Field>
				<Field label="Currency symbol" for="s-sym"><Input id="s-sym" bind:value={form.currency_symbol} maxlength={8} /></Field>
				<Field label="Tax label" for="s-taxl"><Input id="s-taxl" bind:value={form.tax_label} maxlength={20} /></Field>
				<Field label="Default tax rate (%)" for="s-tax" error={errors.default_tax_rate}><Input id="s-tax" type="number" step="0.01" min="0" max="100" bind:value={form.default_tax_rate} /></Field>
				<Field label="Invoice footer" for="s-footer" class="sm:col-span-2"><Input id="s-footer" bind:value={form.invoice_footer} maxlength={300} /></Field>
			</fieldset>
			{#if !readOnly}<div class="mt-4 flex justify-end"><Button type="submit" loading={busy}>Save settings</Button></div>{/if}
		</Card>
	</form>
{/if}
