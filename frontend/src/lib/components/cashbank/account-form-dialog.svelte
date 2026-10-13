<script lang="ts">
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { toast } from '$lib/stores/toast.svelte';
	import { accounts } from '$lib/stores/accounts.svelte';
	import type { AccountKind, MoneyAccount } from '$lib/types';
	import Dialog from '$lib/components/ui/dialog.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import { ACCOUNT_KINDS } from './kinds';

	let {
		open = $bindable(false),
		account = null,
		onSaved
	}: { open?: boolean; account?: MoneyAccount | null; onSaved?: (a: MoneyAccount) => void } = $props();

	const blank = () => ({ name: '', kind: 'BANK' as AccountKind, account_number: '', opening_balance: '0', notes: '' });
	let form = $state(blank());
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);

	$effect(() => {
		if (open) {
			errors = {};
			form = account
				? { name: account.name, kind: account.kind, account_number: account.account_number, opening_balance: account.opening_balance, notes: account.notes }
				: blank();
		}
	});

	async function save(e: SubmitEvent) {
		e.preventDefault();
		e.stopPropagation();
		busy = true;
		errors = {};
		try {
			const res = account
				? await api.patch<MoneyAccount>(`cash-bank/accounts/${account.id}/`, form)
				: await api.post<MoneyAccount>('cash-bank/accounts/', form);
			toast.success(res.message);
			await accounts.refresh();
			open = false;
			onSaved?.(res.data);
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError && !Object.keys(errors).length) errors.name = err.message;
		} finally {
			busy = false;
		}
	}
</script>

<Dialog bind:open title={account ? `Edit ${account.name}` : 'New cash / bank account'} description="Where money is kept: cash in hand, a bank account, eSewa / Khalti and so on.">
	<form id="account-form" class="grid gap-4 sm:grid-cols-2" onsubmit={save}>
		<Field label="Name" for="acc-name" required error={errors.name} class="sm:col-span-2">
			<Input id="acc-name" bind:value={form.name} required maxlength={100} placeholder="e.g. Nabil Bank, eSewa, Petty Cash" aria-invalid={!!errors.name} />
		</Field>
		<Field label="Type" for="acc-kind" error={errors.kind}><Select id="acc-kind" bind:value={form.kind} options={ACCOUNT_KINDS} /></Field>
		<Field label="Account / wallet no." for="acc-no" error={errors.account_number}><Input id="acc-no" bind:value={form.account_number} maxlength={60} /></Field>
		<Field label="Opening balance" for="acc-open" error={errors.opening_balance} hint="Money in this account before your first entry." class="sm:col-span-2">
			<Input id="acc-open" type="number" step="0.01" bind:value={form.opening_balance} class="text-right" />
		</Field>
		<Field label="Notes" for="acc-notes" class="sm:col-span-2"><Textarea id="acc-notes" bind:value={form.notes} rows={2} /></Field>
	</form>
	{#snippet footer()}
		<Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
		<Button type="submit" form="account-form" loading={busy}>{account ? 'Save changes' : 'Create account'}</Button>
	{/snippet}
</Dialog>
