<script lang="ts">
	import { goto } from '$app/navigation';
	import { Save, ArrowRight } from '@lucide/svelte';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { accounts } from '$lib/stores/accounts.svelte';
	import { today } from '$lib/utilities/format';
	import type { Contra } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import AccountSelect from '$lib/components/cashbank/account-select.svelte';

	let date = $state(today());
	let fromAccount = $state<number | null>(null);
	let toAccount = $state<number | null>(null);
	let amount = $state('');
	let reference = $state('');
	let notes = $state('');
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);

	// Most transfers are cash deposited into the bank: start with cash -> first non-cash account.
	accounts.ensure().then(() => {
		toAccount = accounts.active.find((a) => a.kind !== 'CASH')?.id ?? null;
	});

	async function save(e: SubmitEvent) {
		e.preventDefault();
		errors = {};
		if (fromAccount && fromAccount === toAccount) {
			errors.to_account = 'Choose a different account.';
			return;
		}
		busy = true;
		try {
			const res = await api.post<Contra>('cash-bank/contras/', {
				date,
				from_account: fromAccount,
				to_account: toAccount,
				amount,
				reference_number: reference,
				notes
			});
			toast.success(`✓ ${res.message}`);
			goto(`/cash-bank/contra/${res.data.id}`);
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError) toast.error(`✕ ${err.message}`);
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>New contra · Mero Lekhapal</title></svelte:head>

<PageHeader title="New contra" description="Move money between your own accounts, e.g. deposit cash in the bank or withdraw cash." back={{ href: '/cash-bank/contra', label: 'Contra entries' }} />

<form class="grid gap-4" onsubmit={save}>
	<Card>
		<div class="grid gap-4 sm:grid-cols-[1fr_auto_1fr] sm:items-end">
			<Field label="From (money goes out)" for="contra-from" required error={errors.from_account}><AccountSelect id="contra-from" bind:value={fromAccount} /></Field>
			<ArrowRight class="mx-auto mb-2 hidden size-5 text-muted-foreground sm:block" />
			<Field label="To (money comes in)" for="contra-to" required error={errors.to_account}><AccountSelect id="contra-to" bind:value={toAccount} /></Field>
		</div>
		<div class="mt-4 grid gap-4 sm:grid-cols-3">
			<Field label="Date" for="contra-date" required error={errors.date}><Input id="contra-date" type="date" bind:value={date} required /></Field>
			<Field label="Amount ({auth.currency})" for="contra-amount" required error={errors.amount}>
				<Input id="contra-amount" type="number" min="0.01" step="0.01" bind:value={amount} required class="text-right" />
			</Field>
			<Field label="Reference no." for="contra-ref" hint="Deposit slip / cheque no."><Input id="contra-ref" bind:value={reference} maxlength={60} /></Field>
			<Field label="Notes" for="contra-notes" class="sm:col-span-3"><Textarea id="contra-notes" bind:value={notes} rows={2} placeholder="e.g. Cash deposited to Nabil Bank" /></Field>
		</div>
	</Card>
	<div class="flex justify-end"><Button type="submit" size="lg" loading={busy} data-testid="save-contra"><Save />Save contra</Button></div>
</form>
