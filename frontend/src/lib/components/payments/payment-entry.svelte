<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { Save } from '@lucide/svelte';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { today, PAYMENT_METHODS, toCents } from '$lib/utilities/format';
	import type { OpenDocument, Party, PaymentDoc } from '$lib/types';
	import { PAY_SIDES, type PayKind } from './config';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import PartyPicker from '$lib/components/parties/party-picker.svelte';
	import AllocationTable from './allocation-table.svelte';

	let { kind }: { kind: PayKind } = $props();
	// svelte-ignore state_referenced_locally
	const side = PAY_SIDES[kind];

	let partyId = $state<number | null>(null);
	let partyLabel = $state('');
	let date = $state(today());
	let amount = $state('');
	let method = $state('CASH');
	let reference = $state('');
	let notes = $state('');
	let mode = $state<'auto' | 'manual' | 'none'>('auto');
	let docs = $state<OpenDocument[]>([]);
	let amounts = $state<Record<number, string>>({});
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);
	const preDoc = Number(page.url.searchParams.get('document')) || null;

	$effect(() => {
		const pid = page.url.searchParams.get('party');
		if (pid && !partyId) api.get<Party>(`${side.type === 'CUSTOMER' ? 'customers' : 'vendors'}/${pid}/`).then(selectParty).catch(() => {});
	});

	async function selectParty(p: Party | null) {
		partyId = p?.id ?? null;
		partyLabel = p?.name ?? '';
		amounts = {};
		docs = p ? await api.get<OpenDocument[]>(`${side.api}/open-documents/`, { party: p.id }) : [];
		const target = preDoc ? docs.find((d) => d.id === preDoc) : null;
		if (target) {
			mode = 'manual';
			amounts = { [target.id]: target.balance_due };
			if (!amount) amount = target.balance_due;
		}
	}

	async function save(e: SubmitEvent) {
		e.preventDefault();
		errors = {};
		if (!partyId) {
			errors.party = `Select a ${side.partyLabel.toLowerCase()}.`;
			return;
		}
		const allocations =
			mode === 'manual'
				? Object.entries(amounts)
						.filter(([, v]) => toCents(v) > 0n)
						.map(([document, v]) => ({ document: Number(document), amount: v }))
				: [];
		busy = true;
		try {
			const res = await api.post<PaymentDoc>(`${side.api}/`, {
				party: partyId,
				date,
				amount,
				payment_method: method,
				reference_number: reference,
				notes,
				allocations,
				auto_allocate: mode === 'auto'
			});
			toast.success(`✓ ${res.message}`);
			goto(`${side.route}/${res.data.id}`);
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError) toast.error(`✕ ${err.message}`);
		} finally {
			busy = false;
		}
	}
</script>

<PageHeader title="New {side.label.toLowerCase()}" description="Number is assigned on save. Allocate to open {side.docLabel.toLowerCase()}s now or keep as an advance and allocate later." back={{ href: side.route, label: side.title }} />

<form class="grid gap-4" onsubmit={save}>
	<Card>
		<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
			<Field label={side.partyLabel} for="pay-party" required error={errors.party} class="sm:col-span-2">
				<PartyPicker type={side.type} id="pay-party" bind:value={partyId} bind:label={partyLabel} onSelect={selectParty} invalid={!!errors.party} />
			</Field>
			<Field label="Date" for="pay-date" required error={errors.date}><Input id="pay-date" type="date" bind:value={date} required /></Field>
			<Field label="Amount ({auth.currency})" for="pay-amount" required error={errors.amount}>
				<Input id="pay-amount" type="number" min="0.01" step="0.01" bind:value={amount} required class="text-right" />
			</Field>
			<Field label="Payment method" for="pay-method"><Select id="pay-method" bind:value={method} options={PAYMENT_METHODS} /></Field>
			<Field label="Reference no." for="pay-ref" hint="Cheque / bank / transaction ref."><Input id="pay-ref" bind:value={reference} maxlength={60} /></Field>
			<Field label="Notes" for="pay-notes" class="sm:col-span-2"><Textarea id="pay-notes" bind:value={notes} rows={1} /></Field>
		</div>
	</Card>

	<Card title="Allocation" bodyClass="p-0">
		<div class="flex flex-wrap gap-4 border-b p-3 text-sm" role="radiogroup" aria-label="Allocation mode">
			<label class="flex items-center gap-2"><input type="radio" bind:group={mode} value="auto" /> Automatically (oldest due first)</label>
			<label class="flex items-center gap-2"><input type="radio" bind:group={mode} value="manual" data-testid="alloc-manual" /> Manually</label>
			<label class="flex items-center gap-2"><input type="radio" bind:group={mode} value="none" /> Keep as advance (unallocated)</label>
		</div>
		{#if mode === 'manual'}
			<AllocationTable {docs} bind:amounts available={amount || '0'} docLabel={side.docLabel} docRoute={side.docRoute} />
		{:else if partyId}
			<p class="p-3 text-sm text-muted-foreground">{docs.length} open {side.docLabel.toLowerCase()}(s). {mode === 'auto' ? 'The server allocates to the oldest due first; any remainder stays as an advance.' : 'The full amount is recorded as an advance and can be allocated later.'}</p>
		{/if}
	</Card>

	<div class="flex justify-end"><Button type="submit" size="lg" loading={busy} data-testid="save-payment"><Save />Save {side.label.toLowerCase()}</Button></div>
</form>
