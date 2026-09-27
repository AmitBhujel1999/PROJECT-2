<script lang="ts">
	import { Plus, Pencil } from '@lucide/svelte';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { toast } from '$lib/stores/toast.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { dateTime } from '$lib/utilities/format';
	import type { User } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Dialog from '$lib/components/ui/dialog.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import Badge from '$lib/components/ui/badge.svelte';

	const ROLES = [
		{ value: 'ADMIN', label: 'Admin' },
		{ value: 'MANAGER', label: 'Manager' },
		{ value: 'ACCOUNTANT', label: 'Accountant' },
		{ value: 'STAFF', label: 'Staff' }
	];
	const list = new ListState<User>('users/');
	list.load();

	let open = $state(false);
	let editing = $state<User | null>(null);
	let form = $state({ username: '', email: '', first_name: '', last_name: '', phone: '', role: 'STAFF', is_active: true, password: '' });
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);

	function edit(u: User | null) {
		editing = u;
		errors = {};
		form = u
			? { username: u.username, email: u.email, first_name: u.first_name, last_name: u.last_name, phone: u.phone, role: u.role, is_active: u.is_active, password: '' }
			: { username: '', email: '', first_name: '', last_name: '', phone: '', role: 'STAFF', is_active: true, password: '' };
		open = true;
	}

	async function save(e: SubmitEvent) {
		e.preventDefault();
		busy = true;
		errors = {};
		const body: Record<string, unknown> = { ...form };
		if (!form.password) delete body.password;
		try {
			const res = editing ? await api.patch<User>(`users/${editing.id}/`, body) : await api.post<User>('users/', body);
			toast.success(res.message);
			open = false;
			list.load();
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError) toast.error(err.message);
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Users · Accounting</title></svelte:head>
<PageHeader title="Users" description="Users are deactivated rather than deleted. Role changes are recorded in the audit log.">
	{#snippet actions()}<Button onclick={() => edit(null)}><Plus />New user</Button>{/snippet}
</PageHeader>

<Card bodyClass="p-0">
	<div class="border-b p-3"><SearchInput placeholder="Search users…" onSearch={(v) => list.set('search', v)} /></div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>Username</th><th>Name</th><th>Email</th><th>Role</th><th>Status</th><th>Last login</th><th></th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={7} />
				{#each list.items as u (u.id)}
					<tr>
						<td class="font-medium">{u.username}</td>
						<td>{u.display_name}</td>
						<td>{u.email}</td>
						<td><Badge>{u.role}</Badge></td>
						<td><StatusBadge status={u.is_active ? 'ACTIVE' : 'INACTIVE'} /></td>
						<td class="text-xs text-muted-foreground">{dateTime(u.last_login)}</td>
						<td class="text-right"><Button size="sm" variant="ghost" onclick={() => edit(u)} aria-label="Edit {u.username}"><Pencil /></Button></td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>

<Dialog bind:open title={editing ? `Edit ${editing.username}` : 'New user'}>
	<form id="user-form" class="grid gap-4 sm:grid-cols-2" onsubmit={save}>
		<Field label="Username" for="u-username" required error={errors.username}><Input id="u-username" bind:value={form.username} required autocomplete="off" /></Field>
		<Field label="Email" for="u-email" error={errors.email}><Input id="u-email" type="email" bind:value={form.email} /></Field>
		<Field label="First name" for="u-first"><Input id="u-first" bind:value={form.first_name} /></Field>
		<Field label="Last name" for="u-last"><Input id="u-last" bind:value={form.last_name} /></Field>
		<Field label="Role" for="u-role" error={errors.role}><Select id="u-role" bind:value={form.role} options={ROLES} /></Field>
		<Field label="Status" for="u-active"><Select id="u-active" bind:value={form.is_active} options={[{ value: true as any, label: 'Active' }, { value: false as any, label: 'Inactive' }]} /></Field>
		<Field label={editing ? 'New password (optional)' : 'Password'} for="u-pw" required={!editing} error={errors.password} class="sm:col-span-2">
			<Input id="u-pw" type="password" bind:value={form.password} autocomplete="new-password" required={!editing} />
		</Field>
		{#if errors.non_field_errors}<p class="text-sm text-destructive sm:col-span-2">{errors.non_field_errors}</p>{/if}
	</form>
	{#snippet footer()}
		<Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
		<Button type="submit" form="user-form" loading={busy}>Save</Button>
	{/snippet}
</Dialog>
