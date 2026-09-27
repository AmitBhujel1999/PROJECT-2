<script lang="ts">
	import Spinner from './spinner.svelte';
	import { Inbox, CircleAlert } from '@lucide/svelte';

	let {
		loading,
		error,
		empty,
		colspan,
		emptyText = 'No records found.',
		onRetry
	}: { loading: boolean; error?: string | null; empty: boolean; colspan: number; emptyText?: string; onRetry?: () => void } = $props();
</script>

{#if error}
	<tr>
		<td {colspan} class="py-10 text-center">
			<div class="inline-flex flex-col items-center gap-2 text-destructive">
				<CircleAlert class="size-6" />
				<p class="text-sm">{error}</p>
				{#if onRetry}<button class="text-xs underline" onclick={onRetry}>Retry</button>{/if}
			</div>
		</td>
	</tr>
{:else if loading && empty}
	<tr>
		<td {colspan} class="py-10 text-center text-muted-foreground">
			<span class="inline-flex items-center gap-2 text-sm"><Spinner /> Loading…</span>
		</td>
	</tr>
{:else if empty}
	<tr>
		<td {colspan} class="py-10 text-center text-muted-foreground">
			<span class="inline-flex flex-col items-center gap-2 text-sm"><Inbox class="size-6 opacity-60" />{emptyText}</span>
		</td>
	</tr>
{/if}
