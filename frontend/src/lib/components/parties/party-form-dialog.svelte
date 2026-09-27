<script lang="ts">
	import { api, fieldErrors, ApiError } from '$lib/api/client';
	import { toast } from '$lib/stores/toast.svelte';
	import { CREDIT_TERMS } from '$lib/utilities/format';
	import type { Party, PartyType } from '$lib/types';
	import { SIDES } from './config';
	import Dialog from '$lib/components/ui/dialog.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Button from '$lib/components/ui/button.svelte';

	let {
		open = $bindable(false),
		type,
		party = null,
		initialName = '',
		onSaved
	}: { open?: boolean; type: PartyType; party?: Party | null; initialName?: string; onSaved?: (p: Party) => void } = $props();

	const side = $derived(SIDES[type]);
	const blank = () => ({ name: initialName, pan_vat_no: '', phone: '', email: '', address: '', credit_terms: type === 'CUSTOMER' ? 'DAYS_30' : 'DAYS_30', credit_days: 30, credit_limit: '0', notes: '' });
	let form = $state(blank());
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);

	$effect(() => {
		if (open) {
			errors = {};
			form = party
				? { name: party.name, pan_vat_no: party.pan_vat_no, phone: party.phone, email: party.email, address: party.address, credit_terms: party.credit_terms, credit_days: party.credit_days, credit_limit: party.credit_limit, notes: party.notes }
				: blank();
		}
	});

	async function save(e: SubmitEvent) {
		e.preventDefault();
		busy = true;
		errors = {};
		try {
			const res = party ? await api.patch<Party>(`${side.api}/${party.id}/`, form) : await api.post<Party>(`${side.api}/`, form);
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

<Dialog bind:open title={party ? `Edit ${party.name}` : `New ${side.label.toLowerCase()}`} class="max-w-2xl">
	<form id="party-form-{type}" class="grid gap-4 sm:grid-cols-2" onsubmit={save}>
		<Field label="Name" for="pf-name" required error={errors.name} class="sm:col-span-2">
			<Input id="pf-name" bind:value={form.name} required maxlength={200} aria-invalid={!!errors.name} />
		</Field>
		<Field label="PAN/VAT No." for="pf-pan" error={errors.pan_vat_no}><Input id="pf-pan" bind:value={form.pan_vat_no} maxlength={30} /></Field>
		<Field label="Phone" for="pf-phone" error={errors.phone}><Input id="pf-phone" type="tel" bind:value={form.phone} maxlength={30} /></Field>
		<Field label="Email" for="pf-email" error={errors.email}><Input id="pf-email" type="email" bind:value={form.email} /></Field>
		<Field label="Credit terms" for="pf-terms" error={errors.credit_terms}><Select id="pf-terms" bind:value={form.credit_terms} options={CREDIT_TERMS} /></Field>
		{#if form.credit_terms === 'CUSTOM'}
			<Field label="Credit days" for="pf-days" error={errors.credit_days}><Input id="pf-days" type="number" min="0" max="3650" bind:value={form.credit_days} /></Field>
		{/if}
		{#if type === 'CUSTOMER'}
			<Field label="Credit limit" for="pf-limit" error={errors.credit_limit} hint="0 = no limit"><Input id="pf-limit" type="number" step="0.01" min="0" bind:value={form.credit_limit} class="text-right" /></Field>
		{/if}
		<Field label="Address" for="pf-address" class="sm:col-span-2"><Textarea id="pf-address" bind:value={form.address} rows={2} /></Field>
		<Field label="Notes" for="pf-notes" class="sm:col-span-2"><Textarea id="pf-notes" bind:value={form.notes} rows={2} /></Field>
	</form>
	{#snippet footer()}
		<Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
		<Button type="submit" form="party-form-{type}" loading={busy}>{party ? 'Save changes' : `Create ${side.label.toLowerCase()}`}</Button>
	{/snippet}
</Dialog>
