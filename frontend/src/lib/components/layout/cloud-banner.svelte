<script lang="ts">
	import { onMount } from 'svelte';
	import { CloudCheck } from '@lucide/svelte';
	import { cloudMode, loadBook } from '$lib/cloud/cloud';
	import { refreshPending, type Pending } from '$lib/cloud/entry';

	/** Online copy: shows how fresh the data is and that changes need the computer. */
	let updated = $state('');
	let pending = $state<Pending[]>([]);
	const waiting = $derived(pending.filter((p) => p.status === 'waiting').length);
	const failed = $derived(pending.filter((p) => p.status === 'failed'));
	let now = $state(Date.now());
	/** Safari/Chrome tab (not the home-screen app): suggest adding the online copy to the home screen. */
	let tip = $state(false);

	onMount(() => {
		if (!cloudMode) return;
		try {
			const standalone = matchMedia('(display-mode: standalone)').matches || (navigator as { standalone?: boolean }).standalone;
			const inApp = /AccountingApp/.test(navigator.userAgent);
			tip = !standalone && !inApp && localStorage.getItem('ml-home-tip') !== 'done';
		} catch {
			tip = false;
		}
		const refresh = () => {
			loadBook(true).then((b) => (updated = b?.updated ?? '')).catch(() => {});
			refreshPending(true).then((p) => (pending = p)).catch(() => {});
		};
		refresh();
		const tick = setInterval(() => (now = Date.now()), 30_000);
		const reload = setInterval(refresh, 60_000);
		return () => {
			clearInterval(tick);
			clearInterval(reload);
		};
	});

	const ago = $derived.by(() => {
		if (!updated) return '';
		const mins = Math.round((now - new Date(updated).getTime()) / 60000);
		if (mins < 1) return 'just now';
		if (mins < 60) return `${mins} min ago`;
		const hours = Math.round(mins / 60);
		return hours < 48 ? `${hours} h ago` : new Date(updated).toLocaleDateString();
	});
</script>

{#if cloudMode}
	<a href="/link" class="no-print flex items-center justify-center gap-2 bg-amber-100 px-3 py-1.5 text-center text-xs text-amber-900">
		<CloudCheck class="size-3.5 shrink-0" />
		<span>Online copy{ago ? ` · updated ${ago}` : ''}{waiting ? ` · ${waiting} bill${waiting === 1 ? '' : 's'} waiting for the computer` : ''}</span>
	</a>
	{#if tip}
		<div class="no-print flex items-center gap-2 bg-blue-50 px-3 py-2 text-xs text-blue-900">
			<span class="flex-1">
				Open your books anywhere: tap <b>Share</b> → <b>Add to Home Screen</b> on this page (mero-lekhapal.web.app). It works even when the computer is off.
			</span>
			<button type="button" class="rounded px-2 py-1 font-medium hover:bg-blue-100" onclick={() => { tip = false; try { localStorage.setItem('ml-home-tip', 'done'); } catch {} }}>OK</button>
		</div>
	{/if}
	{#each failed as f (f.number)}
		<a href="/{f.kind === 'sale' ? 'sales' : 'receipts'}/p-{f.number}" class="no-print block bg-red-100 px-3 py-1.5 text-center text-xs text-red-800">
			{f.number} could not be booked: {f.message} Ask the shop to fix it on the computer.
		</a>
	{/each}
{/if}
