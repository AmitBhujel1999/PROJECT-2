<script lang="ts">
	import { onMount } from 'svelte';
	import { api, ApiError } from '$lib/api/client';
	import { toast } from '$lib/stores/toast.svelte';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';

	interface BackupFile {
		name: string;
		date: string;
		before_restore: boolean;
		size: number;
		saved_at: string;
	}
	interface Status {
		folder: string | null;
		target: string | null;
		last_backup: string | null;
		last_error: string | null;
		keep_days: number;
		can_pick: boolean;
		can_restart: boolean;
		restore_pending: boolean;
		suggestions: { label: string; path: string }[];
		files: BackupFile[];
	}

	let status = $state<Status | null>(null);
	let problem = $state('');
	let folder = $state('');
	let busy = $state(false);
	let restarting = $state(false);

	async function load() {
		try {
			status = await api.get<Status>('backup/');
			folder = status.folder ?? '';
		} catch (err) {
			problem = err instanceof ApiError ? err.message : 'Could not load.';
		}
	}
	onMount(load);

	async function run(action: () => Promise<{ data: Status; message: string }>) {
		busy = true;
		try {
			const res = await action();
			status = res.data;
			folder = status.folder ?? '';
			toast.success(res.message);
			return true;
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Something went wrong.');
			return false;
		} finally {
			busy = false;
		}
	}

	async function pick() {
		try {
			const res = await api.post<{ folder: string | null }>('backup/pick/');
			if (res.data.folder) folder = res.data.folder;
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Could not open the folder chooser.');
		}
	}

	const save = (path: string | null) => run(() => api.post<Status>('backup/folder/', { folder: path }));
	const backupNow = () => run(() => api.post<Status>('backup/now/'));
	function switchOff() {
		if (!confirm('Switch backups off? Backups already saved stay in the folder.')) return;
		save(null);
	}

	async function restore(f: BackupFile) {
		const ok = confirm(
			`Restore the backup of ${day(f.date)} (saved ${when(f.saved_at)})?\n\n` +
				'Everything entered after that backup will be replaced. Your current books are saved first as a "before restore" backup, so you can go back.\n\n' +
				'Mero Lekhapal will restart and you sign in again.'
		);
		if (!ok) return;
		if ((await run(() => api.post<Status>('backup/restore/', { name: f.name }))) && status?.can_restart) restarting = true;
	}

	const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : 'not yet');
	const day = (iso: string) => new Date(iso + 'T00:00:00').toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
	const size = (bytes: number) => (bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`);
</script>

<svelte:head><title>Backups · Mero Lekhapal</title></svelte:head>

<PageHeader title="Backups" description="Copies of your books in a folder you choose, such as Google Drive, so they are safe if this computer breaks or is lost." />

{#if restarting}
	<Card>
		<div class="p-5 text-sm">Restarting Mero Lekhapal to put the backup in place. Sign in again when it opens.</div>
	</Card>
{:else if problem}
	<Card>
		<div class="p-5 text-sm text-red-700">{problem}</div>
	</Card>
{:else if status}
	<div class="grid gap-4 lg:grid-cols-2">
		<Card>
			<div class="grid gap-3 p-5">
				<div>
					<h2 class="font-semibold">Backup folder</h2>
					<p class="text-sm text-muted-foreground">
						{status.folder
							? `On. Every company is backed up once a day and again when you close the program. The last ${status.keep_days} days are kept.`
							: 'Off. Choose a folder to start.'}
					</p>
				</div>
				<div class="flex gap-2">
					<Input bind:value={folder} placeholder="For example G:\My Drive" aria-label="Backup folder" />
					{#if status.can_pick}<Button variant="outline" onclick={pick} disabled={busy}>Browse…</Button>{/if}
				</div>
				{#if status.suggestions.length}
					<div class="flex flex-wrap items-center gap-2 text-sm">
						<span class="text-muted-foreground">Found on this computer:</span>
						{#each status.suggestions as s (s.path)}
							<Button variant="outline" size="sm" onclick={() => (folder = s.path)} disabled={busy}>{s.label}</Button>
						{/each}
					</div>
				{:else}
					<p class="text-xs text-muted-foreground">
						For Google Drive, install <b>Google Drive for desktop</b> and sign in; its <b>My Drive</b> folder then shows here.
					</p>
				{/if}
				<div class="flex flex-wrap gap-2">
					<Button onclick={() => save(folder)} loading={busy} disabled={!folder.trim() || folder.trim() === status.folder}>Save folder</Button>
					{#if status.folder}
						<Button variant="outline" onclick={backupNow} disabled={busy}>Back up now</Button>
						<Button variant="outline" onclick={switchOff} disabled={busy}>Switch off</Button>
					{/if}
				</div>
				{#if status.folder}
					<dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
						<dt class="text-muted-foreground">Files go to</dt>
						<dd class="break-all">{status.target}</dd>
						<dt class="text-muted-foreground">Last backup</dt>
						<dd>{when(status.last_backup)}</dd>
					</dl>
				{/if}
				{#if status.last_error}
					<p class="rounded-md bg-red-50 px-3 py-2 text-sm text-red-700">Last problem: {status.last_error}</p>
				{/if}
			</div>
		</Card>

		<Card>
			<div class="grid gap-3 p-5">
				<div>
					<h2 class="font-semibold">Restore</h2>
					<p class="text-sm text-muted-foreground">Backups of this company, newest first. Restoring replaces the books with the chosen copy.</p>
				</div>
				{#if status.restore_pending}
					<p class="rounded-md bg-amber-50 px-3 py-2 text-sm text-amber-800">A restore is waiting. Close Mero Lekhapal and start it again to finish.</p>
				{/if}
				{#if status.files.length}
					<ul class="divide-y rounded-md border">
						{#each status.files as f (f.name)}
							<li class="flex items-center justify-between gap-3 px-3 py-2 text-sm">
								<div class="min-w-0">
									<div class="font-medium">{day(f.date)}{f.before_restore ? ' · before restore' : ''}</div>
									<div class="text-xs text-muted-foreground">Saved {when(f.saved_at)} · {size(f.size)}</div>
								</div>
								<Button variant="outline" size="sm" onclick={() => restore(f)} disabled={busy || status.restore_pending}>Restore</Button>
							</li>
						{/each}
					</ul>
				{:else}
					<p class="text-sm text-muted-foreground">No backups yet.</p>
				{/if}
				<p class="text-xs text-muted-foreground">
					On a new computer: install Mero Lekhapal, set up any company, choose the same backup folder here, then restore the newest backup.
				</p>
			</div>
		</Card>
	</div>
{/if}
