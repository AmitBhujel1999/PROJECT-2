<script lang="ts">
	import { onMount } from 'svelte';
	import { CloudCheck } from '@lucide/svelte';
	import { cloudMode, loadBook } from '$lib/cloud/cloud';

	/** Online copy: shows how fresh the data is and that changes need the computer. */
	let updated = $state('');
	let now = $state(Date.now());

	onMount(() => {
		if (!cloudMode) return;
		const refresh = () => loadBook(true).then((b) => (updated = b?.updated ?? '')).catch(() => {});
		refresh();
		const tick = setInterval(() => (now = Date.now()), 30_000);
		const reload = setInterval(refresh, 120_000);
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
		<span>Online copy{ago ? ` · updated ${ago}` : ''} · view only</span>
	</a>
{/if}
