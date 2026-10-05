<script lang="ts">
	import { CircleAlert, Inbox } from '@lucide/svelte';
	import Spinner from '$lib/components/ui/spinner.svelte';

	/** Loading / error / empty message for phone card lists. */
	let {
		loading,
		error,
		empty,
		emptyText = 'No records found.',
		onRetry
	}: { loading: boolean; error?: string | null; empty: boolean; emptyText?: string; onRetry?: () => void } = $props();
</script>

{#if error}
	<div class="flex flex-col items-center gap-2 py-10 text-center text-sm text-destructive">
		<CircleAlert class="size-6" />{error}
		{#if onRetry}<button class="text-xs underline" onclick={onRetry}>Retry</button>{/if}
	</div>
{:else if loading && empty}
	<div class="flex items-center justify-center gap-2 py-10 text-sm text-muted-foreground"><Spinner /> Loading…</div>
{:else if empty}
	<div class="flex flex-col items-center gap-2 py-10 text-sm text-muted-foreground"><Inbox class="size-6 opacity-60" />{emptyText}</div>
{/if}
