<script lang="ts">
	import Badge from './badge.svelte';

	let { status }: { status: string | null | undefined } = $props();

	const map: Record<string, { label: string; variant: 'success' | 'warning' | 'danger' | 'muted' | 'info' | 'default' }> = {
		PAID: { label: 'Paid', variant: 'success' },
		PARTIAL: { label: 'Partial', variant: 'warning' },
		UNPAID: { label: 'Unpaid', variant: 'danger' },
		ACTIVE: { label: 'Active', variant: 'success' },
		CANCELLED: { label: 'Cancelled', variant: 'muted' },
		IN_STOCK: { label: 'In stock', variant: 'success' },
		LOW_STOCK: { label: 'Low stock', variant: 'warning' },
		OUT_OF_STOCK: { label: 'Out of stock', variant: 'danger' },
		INACTIVE: { label: 'Inactive', variant: 'muted' }
	};
	const entry = $derived(status ? (map[status] ?? { label: status, variant: 'default' as const }) : null);
</script>

{#if entry}<Badge variant={entry.variant}>{entry.label}</Badge>{/if}
