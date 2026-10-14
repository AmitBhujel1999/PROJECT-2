<script lang="ts">
	import { CREDIT_TERMS } from '$lib/utilities/format';
	import type { PartyType } from '$lib/types';
	import { SIDES } from './config';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import BulkGrid, { type BulkColumn } from '$lib/components/bulk/bulk-grid.svelte';

	let { type }: { type: PartyType } = $props();
	const side = $derived(SIDES[type]);

	const columns = $derived<BulkColumn[]>([
		{ key: 'name', label: 'Name', required: true, width: 'min-w-56' },
		{ key: 'phone', label: 'Phone', width: 'w-32' },
		{ key: 'email', label: 'Email', width: 'w-48' },
		{ key: 'pan_vat_no', label: 'PAN/VAT No.', width: 'w-32' },
		{ key: 'address', label: 'Address', width: 'min-w-48' },
		{ key: 'credit_terms', label: 'Pays within', kind: 'select', options: CREDIT_TERMS, width: 'w-28' },
		{ key: 'credit_days', label: 'Days (if Custom)', kind: 'number', width: 'w-24' },
		...(type === 'CUSTOMER' ? [{ key: 'credit_limit', label: 'Max credit (0 = no limit)', kind: 'number' as const, width: 'w-28' }] : [])
	]);
</script>

<svelte:head><title>Edit Many {side.plural} · Accounting</title></svelte:head>

<PageHeader title="Edit Many {side.plural}" description="Change details of many {side.plural.toLowerCase()} at once, or add new ones from Excel." back={{ href: side.route, label: side.plural }} />

{#key type}<BulkGrid path={side.api} {columns} noun={side.label.toLowerCase()} />{/key}
