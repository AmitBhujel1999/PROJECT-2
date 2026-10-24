<script lang="ts">
	import { Plus, Pencil, Power, Trash2 } from '@lucide/svelte';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { money } from '$lib/utilities/format';
	import type { AccountGroupOption, LedgerAccount, Paginated } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Dialog from '$lib/components/ui/dialog.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';

	let ledgers = $state<LedgerAccount[]>([]);
	let groups = $state<AccountGroupOption[]>([]);
	let loading = $state(true);
	let error = $state<string | null>(null);
	let showInactive = $state(false);
	const canManage = $derived(auth.can('journal.manage_ledgers'));

	async function load() {
		loading = true;
		error = null;
		try {
			const [page, g] = await Promise.all([
				api.get<Paginated<LedgerAccount>>('journal/ledgers/', { page_size: 500 }),
				groups.length ? Promise.resolve(groups) : api.get<AccountGroupOption[]>('journal/ledgers/groups/')
			]);
			ledgers = page.results;
			groups = g;
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not load the chart of accounts.';
		} finally {
			loading = false;
		}
	}
	load();

	const NATURES = [
		{ key: 'LIABILITY', title: 'Capital & Liabilities' },
		{ key: 'ASSET', title: 'Assets' },
		{ key: 'INCOME', title: 'Income' },
		{ key: 'EXPENSE', title: 'Expenses' }
	];
	const sections = $derived(
		NATURES.map((n) => ({
			...n,
			groups: groups
				.filter((g) => g.nature === n.key)
				.map((g) => ({ ...g, ledgers: ledgers.filter((l) => l.group === g.value && (showInactive || l.is_active)) }))
				.filter((g) => g.ledgers.length)
		})).filter((s) => s.groups.length)
	);

	let formOpen = $state(false);
	let editing = $state<LedgerAccount | null>(null);
	let form = $state({ name: '', group: 'FIXED_ASSETS', opening_balance: '0', opening_side: 'DR', notes: '' });
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);
	let target = $state<LedgerAccount | null>(null);
	let toggleOpen = $state(false);
	let deleteOpen = $state(false);
	const normalSide = (group: string) => (['ASSET', 'EXPENSE'].includes(groups.find((g) => g.value === group)?.nature ?? '') ? 'DR' : 'CR');

	function edit(l: LedgerAccount | null) {
		editing = l;
		errors = {};
		form = l
			? { name: l.name, group: l.group, opening_balance: l.opening_balance, opening_side: l.opening_side, notes: l.notes }
			: { name: '', group: 'FIXED_ASSETS', opening_balance: '0', opening_side: 'DR', notes: '' };
		formOpen = true;
	}

	async function save(e: SubmitEvent) {
		e.preventDefault();
		busy = true;
		errors = {};
		try {
			const res = editing
				? await api.patch<LedgerAccount>(`journal/ledgers/${editing.id}/`, form)
				: await api.post<LedgerAccount>('journal/ledgers/', form);
			toast.success(res.message);
			formOpen = false;
			load();
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError && !Object.keys(errors).length) errors.name = err.message;
		} finally {
			busy = false;
		}
	}

	async function toggleActive() {
		if (!target) return;
		const res = await api.post<LedgerAccount>(`journal/ledgers/${target.id}/${target.is_active ? 'deactivate' : 'activate'}/`);
		toast.success(res.message);
		load();
	}

	async function remove() {
		if (!target) return;
		try {
			const res = await api.delete(`journal/ledgers/${target.id}/`);
			toast.success(res.message);
			load();
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Delete failed.');
		}
	}
</script>

<svelte:head><title>Chart of Accounts · Mero Lekhapal</title></svelte:head>

<PageHeader title="Chart of Accounts" description="Ledgers used in journal vouchers. Cash/bank accounts, customers, vendors and expense categories can be used too." back={{ href: '/journal', label: 'Journal vouchers' }}>
	{#snippet actions()}
		{#if canManage}<Button onclick={() => edit(null)} data-testid="new-ledger"><Plus />New ledger</Button>{/if}
	{/snippet}
</PageHeader>

<label class="mb-3 flex items-center gap-2 text-sm"><input type="checkbox" bind:checked={showInactive} /> Show inactive</label>

{#if loading || error}
	<Card bodyClass="p-0"><table class="table-base"><tbody><TableState {loading} {error} empty={false} colspan={1} onRetry={load} /></tbody></table></Card>
{/if}

<div class="grid gap-4">
	{#each sections as s (s.key)}
		<Card title={s.title} bodyClass="p-0">
			<div class="overflow-x-auto">
				<table class="table-base">
					<thead><tr><th>Ledger</th><th>Group</th><th class="num">Opening</th><th class="num">Balance</th><th>Status</th>{#if canManage}<th class="text-right">Actions</th>{/if}</tr></thead>
					<tbody>
						{#each s.groups as g (g.value)}
							{#each g.ledgers as l (l.id)}
								<tr class:opacity-60={!l.is_active}>
									<td class="font-medium"><a class="text-primary hover:underline" href="/journal/ledgers/{l.id}">{l.name}</a></td>
									<td class="text-muted-foreground">{g.label}</td>
									<td class="num">{money(l.opening_balance, true)} {Number(l.opening_balance) ? (l.opening_side === 'DR' ? 'Dr' : 'Cr') : ''}</td>
									<td class="num font-semibold">{money(l.balance?.amount)} {Number(l.balance?.amount) ? l.balance?.side : ''}</td>
									<td><StatusBadge status={l.is_active ? 'ACTIVE' : 'INACTIVE'} /></td>
									{#if canManage}
										<td class="whitespace-nowrap text-right">
											<Button size="sm" variant="ghost" onclick={() => edit(l)} aria-label="Edit {l.name}"><Pencil /></Button>
											<Button size="sm" variant="ghost" onclick={() => ((target = l), (toggleOpen = true))} aria-label="{l.is_active ? 'Deactivate' : 'Activate'} {l.name}"><Power /></Button>
											<Button size="sm" variant="ghost" onclick={() => ((target = l), (deleteOpen = true))} aria-label="Delete {l.name}"><Trash2 /></Button>
										</td>
									{/if}
								</tr>
							{/each}
						{/each}
					</tbody>
				</table>
			</div>
		</Card>
	{/each}
</div>

<Dialog bind:open={formOpen} title={editing ? `Edit ${editing.name}` : 'New ledger'}>
	<form id="ledger-form" class="grid gap-4 sm:grid-cols-2" onsubmit={save}>
		<Field label="Name" for="lg-name" required error={errors.name} class="sm:col-span-2">
			<Input id="lg-name" bind:value={form.name} required maxlength={100} placeholder="e.g. Vehicle, Partner Loan, Rent Deposit" aria-invalid={!!errors.name} />
		</Field>
		<Field label="Group" for="lg-group" error={errors.group} class="sm:col-span-2">
			<Select id="lg-group" bind:value={form.group} options={groups} onchange={() => !editing && (form.opening_side = normalSide(form.group))} />
		</Field>
		<Field label="Opening balance" for="lg-open" error={errors.opening_balance}>
			<Input id="lg-open" type="number" min="0" step="0.01" bind:value={form.opening_balance} class="text-right" />
		</Field>
		<Field label="Dr / Cr" for="lg-side"><Select id="lg-side" bind:value={form.opening_side} options={[{ value: 'DR', label: 'Dr (debit)' }, { value: 'CR', label: 'Cr (credit)' }]} /></Field>
		<Field label="Notes" for="lg-notes" class="sm:col-span-2"><Textarea id="lg-notes" bind:value={form.notes} rows={2} /></Field>
	</form>
	{#snippet footer()}
		<Button variant="outline" onclick={() => (formOpen = false)}>Cancel</Button>
		<Button type="submit" form="ledger-form" loading={busy}>{editing ? 'Save changes' : 'Create ledger'}</Button>
	{/snippet}
</Dialog>
<ConfirmDialog
	bind:open={toggleOpen}
	title={target?.is_active ? 'Deactivate ledger?' : 'Activate ledger?'}
	message={target?.is_active ? `${target?.name} will no longer be offered in new vouchers. History is kept.` : `${target?.name} will be available again.`}
	confirmLabel={target?.is_active ? 'Deactivate' : 'Activate'}
	destructive={target?.is_active}
	onConfirm={toggleActive}
/>
<ConfirmDialog bind:open={deleteOpen} title="Delete ledger?" message="{target?.name} will be deleted permanently. Ledgers used in vouchers cannot be deleted; deactivate them instead." confirmLabel="Delete" destructive onConfirm={remove} />
