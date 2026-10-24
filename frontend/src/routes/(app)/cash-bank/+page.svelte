<script lang="ts">
	import { Plus, Pencil, Power, Trash2, ArrowLeftRight, BookOpen } from '@lucide/svelte';
	import { api, ApiError } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { accounts } from '$lib/stores/accounts.svelte';
	import { money, fromCents, toCents } from '$lib/utilities/format';
	import type { MoneyAccount } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import AccountFormDialog from '$lib/components/cashbank/account-form-dialog.svelte';

	let error = $state<string | null>(null);
	const load = () => accounts.refresh().catch((err) => (error = err instanceof ApiError ? err.message : 'Could not load accounts.'));
	load();

	const canManage = $derived(auth.can('cashbank.manage'));
	let showInactive = $state(false);
	const rows = $derived(accounts.all.filter((a) => showInactive || a.is_active));
	const total = $derived(fromCents(accounts.active.reduce((sum, a) => sum + toCents(a.balance), 0n)));

	let formOpen = $state(false);
	let editing = $state<MoneyAccount | null>(null);
	let target = $state<MoneyAccount | null>(null);
	let toggleOpen = $state(false);
	let deleteOpen = $state(false);

	function edit(a: MoneyAccount | null) {
		editing = a;
		formOpen = true;
	}

	async function toggleActive() {
		if (!target) return;
		const res = await api.post<MoneyAccount>(`cash-bank/accounts/${target.id}/${target.is_active ? 'deactivate' : 'activate'}/`);
		toast.success(res.message);
		load();
	}

	async function remove() {
		if (!target) return;
		try {
			const res = await api.delete(`cash-bank/accounts/${target.id}/`);
			toast.success(res.message);
			load();
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Delete failed.');
		}
	}
</script>

<svelte:head><title>Cash & Bank · Mero Lekhapal</title></svelte:head>

<PageHeader title="Cash & Bank" description="Where your money is kept. Every receipt, payment and expense names one of these accounts.">
	{#snippet actions()}
		{#if auth.can('contra.create')}<Button variant="outline" href="/cash-bank/contra/new"><ArrowLeftRight />Contra (transfer)</Button>{/if}
		{#if canManage}<Button onclick={() => edit(null)} data-testid="new-account"><Plus />New account</Button>{/if}
	{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-center justify-between gap-2 border-b p-3">
		<label class="flex items-center gap-2 text-sm"><input type="checkbox" bind:checked={showInactive} /> Show inactive</label>
		<p class="text-sm">Total in all accounts: <span class="font-semibold tabular-nums" data-testid="accounts-total">{auth.currency} {money(total)}</span></p>
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead>
				<tr><th>Account</th><th>Type</th><th>Account no.</th><th class="num">Opening</th><th class="num">Balance</th><th>Status</th><th class="text-right">Actions</th></tr>
			</thead>
			<tbody>
				<TableState loading={!accounts.loaded && !error} {error} empty={accounts.loaded && !rows.length} colspan={7} onRetry={load} emptyText="No accounts yet." />
				{#each rows as a (a.id)}
					<tr class:opacity-60={!a.is_active}>
						<td class="font-medium"><a class="text-primary hover:underline" href="/cash-bank/{a.id}">{a.name}</a></td>
						<td>{a.kind_display}</td>
						<td class="text-muted-foreground">{a.account_number}</td>
						<td class="num">{money(a.opening_balance)}</td>
						<td class="num font-semibold" class:text-destructive={a.balance?.startsWith('-')}>{money(a.balance)}</td>
						<td><StatusBadge status={a.is_active ? 'ACTIVE' : 'INACTIVE'} /></td>
						<td class="whitespace-nowrap text-right">
							<Button size="sm" variant="ghost" href="/cash-bank/{a.id}" aria-label="Book for {a.name}"><BookOpen /></Button>
							{#if canManage}
								<Button size="sm" variant="ghost" onclick={() => edit(a)} aria-label="Edit {a.name}"><Pencil /></Button>
								<Button size="sm" variant="ghost" onclick={() => ((target = a), (toggleOpen = true))} aria-label="{a.is_active ? 'Deactivate' : 'Activate'} {a.name}"><Power /></Button>
								<Button size="sm" variant="ghost" onclick={() => ((target = a), (deleteOpen = true))} aria-label="Delete {a.name}"><Trash2 /></Button>
							{/if}
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
</Card>

<AccountFormDialog bind:open={formOpen} account={editing} />
<ConfirmDialog
	bind:open={toggleOpen}
	title={target?.is_active ? 'Deactivate account?' : 'Activate account?'}
	message={target?.is_active ? `${target?.name} will no longer be offered for new entries. Its history is kept.` : `${target?.name} will be available again.`}
	confirmLabel={target?.is_active ? 'Deactivate' : 'Activate'}
	destructive={target?.is_active}
	onConfirm={toggleActive}
/>
<ConfirmDialog bind:open={deleteOpen} title="Delete account?" message="{target?.name} will be deleted permanently. Accounts that have entries cannot be deleted; deactivate them instead." confirmLabel="Delete" destructive onConfirm={remove} />
