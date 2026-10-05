<script lang="ts">
	import { Plus, Pencil, Power, Trash2 } from '@lucide/svelte';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import type { ExpenseCategory } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Dialog from '$lib/components/ui/dialog.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';

	const list = new ListState<ExpenseCategory>('expense-categories/', { is_active: 'true' });
	list.load();
	const canManage = $derived(auth.can('expenses.manage_categories'));

	let active = $state('true');
	let formOpen = $state(false);
	let editing = $state<ExpenseCategory | null>(null);
	let form = $state({ name: '', description: '' });
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);
	let target = $state<ExpenseCategory | null>(null);
	let toggleOpen = $state(false);
	let deleteOpen = $state(false);

	function edit(c: ExpenseCategory | null) {
		editing = c;
		errors = {};
		form = c ? { name: c.name, description: c.description } : { name: '', description: '' };
		formOpen = true;
	}

	async function save(e: SubmitEvent) {
		e.preventDefault();
		busy = true;
		errors = {};
		try {
			const res = editing
				? await api.patch<ExpenseCategory>(`expense-categories/${editing.id}/`, form)
				: await api.post<ExpenseCategory>('expense-categories/', form);
			toast.success(res.message);
			formOpen = false;
			list.load();
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError && !Object.keys(errors).length) errors.name = err.message;
		} finally {
			busy = false;
		}
	}

	async function toggleActive() {
		if (!target) return;
		const res = await api.post<ExpenseCategory>(`expense-categories/${target.id}/${target.is_active ? 'deactivate' : 'activate'}/`);
		toast.success(res.message);
		list.load();
	}

	async function remove() {
		if (!target) return;
		try {
			const res = await api.delete(`expense-categories/${target.id}/`);
			toast.success(res.message);
			list.load();
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Delete failed.');
		}
	}
</script>

<svelte:head><title>Expense Categories · Accounting</title></svelte:head>

<PageHeader title="Expense Categories" description="Group expenses for reporting. Categories with expenses are deactivated, never deleted.">
	{#snippet actions()}
		{#if canManage}<Button onclick={() => edit(null)} data-testid="new-category"><Plus />New category</Button>{/if}
	{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-center gap-2 border-b p-3">
		<SearchInput placeholder="Search categories…" onSearch={(v) => list.set('search', v)} />
		<Select class="w-36" bind:value={active} onchange={() => list.set('is_active', active)} aria-label="Status filter"
			options={[{ value: 'true', label: 'Active' }, { value: 'false', label: 'Inactive' }]} placeholder="All" />
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead>
				<tr><th>Name</th><th>Description</th><th class="num">Expenses</th><th>Status</th>{#if canManage}<th class="text-right">Actions</th>{/if}</tr>
			</thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={5} onRetry={() => list.load()} />
				{#each list.items as c (c.id)}
					<tr>
						<td class="font-medium"><a class="text-primary hover:underline" href="/reports/expenses?category={c.id}">{c.name}</a></td>
						<td class="text-muted-foreground">{c.description}</td>
						<td class="num">{c.expense_count ?? 0}</td>
						<td><StatusBadge status={c.is_active ? 'ACTIVE' : 'INACTIVE'} /></td>
						{#if canManage}
							<td class="whitespace-nowrap text-right">
								<Button size="sm" variant="ghost" onclick={() => edit(c)} aria-label="Edit {c.name}"><Pencil /></Button>
								<Button size="sm" variant="ghost" onclick={() => ((target = c), (toggleOpen = true))} aria-label="{c.is_active ? 'Deactivate' : 'Activate'} {c.name}"><Power /></Button>
								{#if !c.expense_count}
									<Button size="sm" variant="ghost" onclick={() => ((target = c), (deleteOpen = true))} aria-label="Delete {c.name}"><Trash2 /></Button>
								{/if}
							</td>
						{/if}
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>

<Dialog bind:open={formOpen} title={editing ? `Edit ${editing.name}` : 'New expense category'}>
	<form id="category-form" class="grid gap-4" onsubmit={save}>
		<Field label="Name" for="cat-name" required error={errors.name}>
			<Input id="cat-name" bind:value={form.name} required maxlength={100} aria-invalid={!!errors.name} />
		</Field>
		<Field label="Description" for="cat-desc" error={errors.description}>
			<Input id="cat-desc" bind:value={form.description} maxlength={255} />
		</Field>
	</form>
	{#snippet footer()}
		<Button variant="outline" onclick={() => (formOpen = false)}>Cancel</Button>
		<Button type="submit" form="category-form" loading={busy}>{editing ? 'Save changes' : 'Create category'}</Button>
	{/snippet}
</Dialog>
<ConfirmDialog
	bind:open={toggleOpen}
	title={target?.is_active ? 'Deactivate category?' : 'Activate category?'}
	message={target?.is_active ? `${target?.name} will no longer be available for new expenses. History is kept.` : `${target?.name} will be available again.`}
	confirmLabel={target?.is_active ? 'Deactivate' : 'Activate'}
	destructive={target?.is_active}
	onConfirm={toggleActive}
/>
<ConfirmDialog bind:open={deleteOpen} title="Delete category?" message="{target?.name} has no expenses and will be deleted permanently." confirmLabel="Delete" destructive onConfirm={remove} />
