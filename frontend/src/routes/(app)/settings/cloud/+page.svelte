<script lang="ts">
	import { onMount } from 'svelte';
	import { api, ApiError } from '$lib/api/client';
	import { toast } from '$lib/stores/toast.svelte';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';

	interface Device {
		uid: string;
		name: string;
		username: string;
		series: string;
		added_at: string;
	}
	interface InboxItem {
		number: string;
		kind: 'sale' | 'receipt';
		device: string;
		status: 'new' | 'booked' | 'failed';
		message: string;
		doc_id: number;
		received_at: string;
	}
	interface Status {
		enabled: boolean;
		app_url: string;
		last_sync: string | null;
		last_error: string | null;
		pages: number;
		phone_entry: boolean;
		devices: Device[];
		inbox: InboxItem[];
	}

	let status = $state<Status | null>(null);
	let busy = $state(false);

	async function load() {
		try {
			status = await api.get<Status>('cloud/');
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Could not load.');
		}
	}

	onMount(() => {
		load();
		const timer = setInterval(load, 10_000);
		return () => clearInterval(timer);
	});

	async function run(action: () => Promise<{ data: Status; message: string }>) {
		busy = true;
		try {
			const res = await action();
			status = res.data;
			toast.success(res.message);
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Something went wrong.');
		} finally {
			busy = false;
		}
	}

	const toggle = () => run(() => api.post<Status>('cloud/enable/', { enabled: !status?.enabled }));
	const syncNow = () => run(() => api.post<Status>('cloud/sync/'));
	const toggleEntry = () => run(() => api.post<Status>('cloud/entry/', { enabled: !status?.phone_entry }));
	const retry = (i: InboxItem) => run(() => api.post<Status>(`cloud/inbox/${i.number}/retry/`));
	const link = (i: InboxItem) => (i.kind === 'sale' ? `/sales/${i.doc_id}` : `/receipts/${i.doc_id}`);
	function remove(d: Device) {
		if (!confirm(`Remove ${d.name || 'this phone'} (${d.username})? It will no longer open the online copy.`)) return;
		run(() => api.delete<Status>(`cloud/devices/${d.uid}/`));
	}

	const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : 'not yet');
</script>

<svelte:head><title>Phone Anywhere · Mero Lekhapal</title></svelte:head>

<PageHeader title="Phone Anywhere" description="An online copy of your books, so linked phones can see them on any Wi-Fi or mobile data." />

{#if status}
	<div class="grid gap-4 lg:grid-cols-2">
		<Card>
			<div class="grid gap-3 p-5">
				<div class="flex items-center justify-between gap-3">
					<div>
						<h2 class="font-semibold">Online copy</h2>
						<p class="text-sm text-muted-foreground">
							{status.enabled ? 'On. This computer uploads changes about every minute while it is online.' : 'Off. Nothing is uploaded.'}
						</p>
					</div>
					<Button variant={status.enabled ? 'outline' : 'default'} onclick={toggle} loading={busy}>{status.enabled ? 'Switch off' : 'Switch on'}</Button>
				</div>
				{#if status.enabled}
					<dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
						<dt class="text-muted-foreground">Last upload</dt>
						<dd>{when(status.last_sync)}</dd>
						<dt class="text-muted-foreground">Pages online</dt>
						<dd>{status.pages}</dd>
						<dt class="text-muted-foreground">Phone address</dt>
						<dd><a class="text-primary hover:underline" href={status.app_url} target="_blank">{status.app_url.replace('https://', '')}</a></dd>
					</dl>
					{#if status.last_error}
						<p class="rounded-md bg-red-50 px-3 py-2 text-sm text-red-700">Last problem: {status.last_error}</p>
					{/if}
					<div><Button variant="outline" size="sm" onclick={syncNow} disabled={busy}>Upload now</Button></div>
				{/if}
				<p class="text-xs text-muted-foreground">
					The copy is stored in Google Firebase (Mumbai). Phones see only the pages their user's role allows. PDFs, exports and
					changes other than sales and receipts still need the computer.
				</p>
			</div>
		</Card>

		<Card>
			<div class="grid gap-3 p-5">
				<div class="flex items-center justify-between gap-3">
					<div>
						<h2 class="font-semibold">Sales and receipts on phones</h2>
						<p class="text-sm text-muted-foreground">
							{status.phone_entry
								? 'Allowed. Each phone numbers its bills in its own series (M1-000001, receipts M1-R-000001); they are booked here when this computer is online.'
								: 'Off. Phones can only view the books.'}
						</p>
					</div>
					<Button variant="outline" onclick={toggleEntry} disabled={busy}>{status.phone_entry ? 'Switch off' : 'Allow'}</Button>
				</div>
				{#if status.inbox.length}
					<ul class="divide-y rounded-md border">
						{#each status.inbox as i (i.number)}
							<li class="flex items-center justify-between gap-3 px-3 py-2 text-sm">
								<div class="min-w-0">
									<div class="font-medium">
										{#if i.status === 'booked'}<a class="text-primary hover:underline" href={link(i)}>{i.number}</a>{:else}{i.number}{/if}
										<span class="text-muted-foreground">· {i.kind === 'sale' ? 'Sale' : 'Receipt'} · {when(i.received_at)}</span>
									</div>
									{#if i.status === 'failed'}<div class="text-xs text-red-700">{i.message}</div>{/if}
								</div>
								{#if i.status === 'failed'}
									<Button size="sm" onclick={() => retry(i)} disabled={busy}>Try again</Button>
								{:else}
									<span class="text-xs {i.status === 'booked' ? 'text-green-700' : 'text-muted-foreground'}">{i.status === 'booked' ? 'Booked' : 'Waiting'}</span>
								{/if}
							</li>
						{/each}
					</ul>
					<p class="text-xs text-muted-foreground">A bill that could not be booked (for example, not enough stock here) was already given to the customer: fix the cause, then press Try again.</p>
				{/if}
			</div>
		</Card>

		<Card>
			<div class="grid gap-3 p-5">
				<div>
					<h2 class="font-semibold">Linked phones</h2>
					<p class="text-sm text-muted-foreground">
						To link a phone: connect it to this Wi-Fi, open Mero Lekhapal on it, sign in, then tap <b>More → Use this phone anywhere</b>.
					</p>
				</div>
				{#if status.devices.length}
					<ul class="divide-y rounded-md border">
						{#each status.devices as d (d.uid)}
							<li class="flex items-center justify-between gap-3 px-3 py-2 text-sm">
								<div>
									<div class="font-medium">{d.name || 'Phone'} · {d.username}{d.series ? ` · bills ${d.series}-…` : ''}</div>
									<div class="text-xs text-muted-foreground">Linked {when(d.added_at)}</div>
								</div>
								<Button variant="outline" size="sm" onclick={() => remove(d)} disabled={busy}>Remove</Button>
							</li>
						{/each}
					</ul>
				{:else}
					<p class="text-sm text-muted-foreground">No phones linked yet.</p>
				{/if}
			</div>
		</Card>
	</div>
{/if}
