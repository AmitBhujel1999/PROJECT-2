<script lang="ts">
	import { ListState } from '$lib/stores/list.svelte';
	import { dateTime } from '$lib/utilities/format';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import SearchInput from '$lib/components/ui/search-input.svelte';
	import Pagination from '$lib/components/ui/pagination.svelte';
	import TableState from '$lib/components/ui/table-state.svelte';
	import Dialog from '$lib/components/ui/dialog.svelte';
	import Badge from '$lib/components/ui/badge.svelte';

	interface Entry { id: number; username: string; action: string; model_name: string; object_id: string; object_repr: string; timestamp: string; ip_address: string | null; before_data: unknown; after_data: unknown }

	const ACTIONS = ['CREATE', 'UPDATE', 'CANCEL', 'ACTIVATE', 'DEACTIVATE', 'ALLOCATE', 'LOGIN', 'LOGIN_FAILED', 'LOGOUT', 'PASSWORD_CHANGE', 'PASSWORD_RESET', 'PERMISSION_CHANGE', 'DELETE'].map((a) => ({ value: a, label: a }));
	const MODELS = ['sales.Sale', 'purchases.Purchase', 'receivables.CustomerReceipt', 'payables.VendorPayment', 'inventory.StockAdjustment', 'products.Product', 'parties.Party', 'users.User', 'common.BusinessSettings'].map((m) => ({ value: m, label: m }));
	const list = new ListState<Entry>('audit-logs/');
	list.load();
	let action = $state('');
	let model = $state('');
	let start = $state('');
	let end = $state('');
	let selected = $state<Entry | null>(null);
	let open = $state(false);
</script>

<svelte:head><title>Audit Log · Accounting</title></svelte:head>
<PageHeader title="Audit log" description="Append-only record of who changed what, when and from where." />

<Card bodyClass="p-0">
	<div class="flex flex-wrap items-end gap-2 border-b p-3">
		<SearchInput placeholder="User, object, reference…" onSearch={(v) => list.set('search', v)} />
		<Select class="w-44" bind:value={action} onchange={() => list.set('action', action)} options={ACTIONS} placeholder="All actions" aria-label="Action" />
		<Select class="w-56" bind:value={model} onchange={() => list.set('model_name', model)} options={MODELS} placeholder="All records" aria-label="Model" />
		<label class="grid gap-1 text-xs text-muted-foreground">From<Input type="date" bind:value={start} class="h-9 w-40" onchange={() => list.set('start_date', start)} /></label>
		<label class="grid gap-1 text-xs text-muted-foreground">To<Input type="date" bind:value={end} class="h-9 w-40" onchange={() => list.set('end_date', end)} /></label>
	</div>
	<div class="overflow-x-auto">
		<table class="table-base">
			<thead><tr><th>Time</th><th>User</th><th>Action</th><th>Record</th><th>Object</th><th>IP</th><th></th></tr></thead>
			<tbody>
				<TableState loading={list.loading} error={list.error} empty={!list.items.length} colspan={7} />
				{#each list.items as e (e.id)}
					<tr>
						<td class="whitespace-nowrap text-xs">{dateTime(e.timestamp)}</td>
						<td>{e.username || '—'}</td>
						<td><Badge variant={e.action === 'LOGIN_FAILED' ? 'danger' : e.action === 'CANCEL' ? 'warning' : 'default'}>{e.action}</Badge></td>
						<td class="whitespace-nowrap font-mono text-xs">{e.model_name}</td>
						<td>{e.object_repr} <span class="text-xs text-muted-foreground">#{e.object_id}</span></td>
						<td class="text-xs">{e.ip_address ?? ''}</td>
						<td>{#if e.before_data || e.after_data}<button class="text-xs text-primary underline" onclick={() => ((selected = e), (open = true))}>Details</button>{/if}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
	<Pagination page={list.page} totalPages={list.totalPages} count={list.count} pageSize={list.pageSize} onPage={list.setPage} onPageSize={list.setPageSize} />
</Card>

<Dialog bind:open title="Audit entry #{selected?.id}" description="{selected?.action} {selected?.model_name} #{selected?.object_id}" class="max-w-4xl">
	<div class="grid gap-3 md:grid-cols-2">
		<div><p class="mb-1 text-xs font-semibold uppercase text-muted-foreground">Before</p><pre class="max-h-96 overflow-auto rounded-md bg-muted p-2 text-xs">{JSON.stringify(selected?.before_data, null, 2)}</pre></div>
		<div><p class="mb-1 text-xs font-semibold uppercase text-muted-foreground">After</p><pre class="max-h-96 overflow-auto rounded-md bg-muted p-2 text-xs">{JSON.stringify(selected?.after_data, null, 2)}</pre></div>
	</div>
</Dialog>
