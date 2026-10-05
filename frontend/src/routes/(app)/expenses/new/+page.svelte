<script lang="ts">
	import { goto } from '$app/navigation';
	import { Save } from '@lucide/svelte';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { debounce } from '$lib/utilities/debounce';
	import { money, today, PAYMENT_METHODS } from '$lib/utilities/format';
	import type { Expense, ExpenseCategory, Paginated } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import PartyPicker from '$lib/components/parties/party-picker.svelte';

	interface Totals {
		amount: string;
		tax_rate: string;
		tax_amount: string;
		total_amount: string;
	}

	let categories = $state<{ value: number; label: string }[]>([]);
	api.get<Paginated<ExpenseCategory>>('expense-categories/', { is_active: 'true', page_size: 100 }).then((d) => {
		categories = d.results.map((c) => ({ value: c.id, label: c.name }));
	});

	let date = $state(today());
	let category = $state('');
	let description = $state('');
	let amount = $state('');
	let taxRate = $state('0');
	let vendorId = $state<number | null>(null);
	let vendorLabel = $state('');
	let payee = $state('');
	let method = $state('CASH');
	let reference = $state('');
	let notes = $state('');
	let totals = $state<Totals | null>(null);
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);

	// Tax and total always come from the server, never from browser arithmetic.
	const preview = debounce(async (a: string, rate: string) => {
		if (!(Number(a) > 0)) {
			totals = null;
			return;
		}
		try {
			totals = (await api.post<Totals>('expenses/calculate/', { amount: a, tax_rate: rate || '0' })).data;
		} catch {
			totals = null;
		}
	}, 300);
	$effect(() => preview(amount, taxRate));

	async function save(e: SubmitEvent) {
		e.preventDefault();
		errors = {};
		if (!category) {
			errors.category = 'Select a category.';
			return;
		}
		busy = true;
		try {
			const res = await api.post<Expense>('expenses/', {
				date,
				category: Number(category),
				description,
				amount,
				tax_rate: taxRate || '0',
				vendor: vendorId,
				payee: vendorId ? '' : payee,
				payment_method: method,
				reference_number: reference,
				notes
			});
			toast.success(`✓ ${res.message}`);
			goto(`/expenses/${res.data.id}`);
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError) toast.error(`✕ ${err.message}`);
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>New expense · Accounting</title></svelte:head>

<PageHeader title="New expense" description="Number is assigned on save. Tax and total are calculated by the server." back={{ href: '/expenses', label: 'Expenses' }} />

<form class="grid gap-4" onsubmit={save}>
	<Card>
		<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
			<Field label="Date" for="exp-date" required error={errors.date}><Input id="exp-date" type="date" bind:value={date} required /></Field>
			<Field label="Category" for="exp-category" required error={errors.category}>
				<Select id="exp-category" bind:value={category} options={categories} placeholder="Select category" aria-invalid={!!errors.category} data-testid="expense-category" />
			</Field>
			<Field label="Description" for="exp-desc" required error={errors.description} class="sm:col-span-2">
				<Input id="exp-desc" bind:value={description} required maxlength={255} placeholder="e.g. Electricity bill for September" />
			</Field>
			<Field label="Amount excl. tax ({auth.currency})" for="exp-amount" required error={errors.amount}>
				<Input id="exp-amount" type="number" min="0.01" step="0.01" bind:value={amount} required class="text-right" />
			</Field>
			<Field label="{auth.taxLabel} rate (%)" for="exp-tax" error={errors.tax_rate} hint="0 when the bill has no {auth.taxLabel}.">
				<Input id="exp-tax" type="number" min="0" max="100" step="0.01" bind:value={taxRate} class="text-right" />
			</Field>
			<Field label="Payment method" for="exp-method"><Select id="exp-method" bind:value={method} options={PAYMENT_METHODS} /></Field>
			<Field label="Reference no." for="exp-ref" hint="Bill / cheque / transaction ref."><Input id="exp-ref" bind:value={reference} maxlength={60} /></Field>
			<Field label="Vendor (optional)" for="exp-vendor" error={errors.vendor} class="sm:col-span-2">
				<PartyPicker type="VENDOR" id="exp-vendor" bind:value={vendorId} bind:label={vendorLabel} />
			</Field>
			<Field label="Paid to" for="exp-payee" error={errors.payee} hint={vendorId ? 'Using the selected vendor.' : 'When the payee is not a registered vendor.'}>
				<Input id="exp-payee" bind:value={payee} maxlength={200} disabled={!!vendorId} />
			</Field>
			<Field label="Notes" for="exp-notes"><Textarea id="exp-notes" bind:value={notes} rows={1} /></Field>
		</div>
	</Card>

	<div class="flex flex-wrap items-center justify-end gap-6">
		<dl class="flex gap-6 text-sm tabular-nums" data-testid="expense-totals">
			<div><dt class="text-xs text-muted-foreground">Amount</dt><dd>{money(totals?.amount ?? '0')}</dd></div>
			<div><dt class="text-xs text-muted-foreground">{auth.taxLabel}</dt><dd>{money(totals?.tax_amount ?? '0')}</dd></div>
			<div><dt class="text-xs text-muted-foreground">Total ({auth.currency})</dt><dd class="text-lg font-semibold">{money(totals?.total_amount ?? '0')}</dd></div>
		</dl>
		<Button type="submit" size="lg" loading={busy} data-testid="save-expense"><Save />Save expense</Button>
	</div>
</form>
