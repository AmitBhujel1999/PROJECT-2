<script lang="ts">
	import { Plus, Pencil, Power } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { ListState } from '$lib/stores/list.svelte';
	import { money } from '$lib/utilities/format';
	import type { Party, PartyType } from '$lib/types';
	import { SIDES } from './config';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import StatusBadge from '$lib/components/ui/status-badge.svelte';
	import ConfirmDialog from '$lib/components/ui/confirm-dialog.svelte';
	import PartyFormDialog from './party-form-dialog.svelte';
	import MCard from '$lib/components/mobile/m-card.svelte';
	import MState from '$lib/components/mobile/m-state.svelte';
	import { phone } from '$lib/stores/viewport.svelte';
	import { page } from '$app/state';

	let { type }: { type: PartyType } = $props();
	// svelte-ignore state_referenced_locally
	const side = SIDES[type];
	const list = new ListState<Party>(`${side.api}/`, { is_active: 'true' });
	list.load();

	let formOpen = $state(false);
	let editing = $state<Party | null>(null);
	let confirmOpen = $state(false);
	let target = $state<Party | null>(null);
	let active = $state('true');

	async function toggle() {
		if (!target) return;
		const res = await api.post<Party>(`${side.api}/${target.id}/${target.is_active ? 'deactivate' : 'activate'}/`);
		toast.success(res.message);
		list.load();
	}

	// Opened from the phone "+ New" panel.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && auth.can('parties.create')) {
			editing = null;
			formOpen = true;
		}
	});
</script>

<PageHeader title={side.plural} description="Search by name, phone, PAN/VAT or email.">
	{#snippet actions()}
		{#if auth.can('parties.create')}<Button onclick={() => ((editing = null), (formOpen = true))} data-testid="new-party"><Plus />New {side.label.toLowerCase()}</Button>{/if}
	{/snippet}
</PageHeader>

{#if phone.current}
	<div class="mb-2 grid gap-2">
		<SearchInput placeholder="Name, phone, PAN…" onSearch={(v) => list.set('search', v)} /></div>
	<MState loading={list.loading} error={list.error} empty={!list.items.length} onRetry={() => list.load()} />
	<div>
		{#each list.items as p (p.id)}
			<MCard href="{side.route}/{p.id}" title={p.name} subtitle={[p.phone, p.pan_vat_no && `PAN ${p.pan_vat_no}`].filter(Boolean).join(' · ')}
				meta={p.credit_terms_display} muted={!p.is_active}>
				{#snippet leading()}<span class="m-avatar">{p.name.split(/\s+/).slice(0, 2).map((w) => w[0]).join('').toUpperCase()}</span>{/snippet}
				{#snippet badge()}{#if !p.is_active}<StatusBadge status="INACTIVE" />{/if}{/snippet}
			</MCard>
		{/each}
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
{:else}
<Card bodyClass="p-0">
	<div class="flex flex-wrap gap-2 border-b p-3">
		<SearchInput placeholder="Search {side.plural.toLowerCase()}…" onSearch={(v) => list.set('search', v)} />
		<Select class="w-36" bind:value={active} onchange={() => list.set('is_active', active)} aria-label="Status filter"
			options={[{ value: 'true', label: 'Active' }, { value: 'false', label: 'Inactive' }]} placeholder="All" />
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>Name</th><th>Phone</th><th>PAN/VAT</th><th>Credit terms</th>{#if type === 'CUSTOMER'}<th class="num">Credit limit</th>{/if}<th>Status</th><th class="text-right">Actions</th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={7} />
				{#each list.items as p (p.id)}
					<tr>
						<td><a href="{side.route}/{p.id}" class="font-medium text-primary hover:underline">{p.name}</a>{#if p.email}<div class="text-xs text-muted-foreground">{p.email}</div>{/if}</td>
						<td class="whitespace-nowrap">{p.phone}</td>
						<td>{p.pan_vat_no}</td>
						<td>{p.credit_terms_display}{#if p.credit_terms === 'CUSTOM'} ({p.credit_days}d){/if}</td>
						{#if type === 'CUSTOMER'}<td class="num">{money(p.credit_limit, true)}</td>{/if}
						<td><StatusBadge status={p.is_active ? 'ACTIVE' : 'INACTIVE'} /></td>
						<td class="whitespace-nowrap text-right">
							{#if auth.can('parties.manage')}
								<Button size="sm" variant="ghost" onclick={() => ((editing = p), (formOpen = true))} aria-label="Edit {p.name}"><Pencil /></Button>
								<Button size="sm" variant="ghost" onclick={() => ((target = p), (confirmOpen = true))} aria-label="Toggle {p.name}"><Power /></Button>
							{/if}
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>
{/if}

<PartyFormDialog bind:open={formOpen} {type} party={editing} onSaved={() => list.load()} />
<ConfirmDialog bind:open={confirmOpen} title={target?.is_active ? `Deactivate ${side.label.toLowerCase()}?` : `Activate ${side.label.toLowerCase()}?`}
	message={target?.is_active ? `${target?.name} will be hidden from new transactions. All history is kept.` : `${target?.name} will be available again.`}
	confirmLabel={target?.is_active ? 'Deactivate' : 'Activate'} destructive={target?.is_active} onConfirm={toggle} />
