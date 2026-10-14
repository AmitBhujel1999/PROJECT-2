<script lang="ts">
	import { UNITS } from '$lib/utilities/format';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import BulkGrid, { type BulkColumn } from '$lib/components/bulk/bulk-grid.svelte';

	const columns: BulkColumn[] = [
		{ key: 'name', label: 'Item name', required: true, width: 'min-w-56' },
		{ key: 'sku_code', label: 'Item code', required: true, width: 'w-32' },
		{ key: 'unit', label: 'Unit', kind: 'select', options: UNITS, width: 'w-24' },
		{ key: 'purchase_price', label: 'Buying price', kind: 'number', width: 'w-28' },
		{ key: 'selling_price', label: 'Selling price', kind: 'number', width: 'w-28' },
		{ key: 'tax_rate', label: 'VAT %', kind: 'number', width: 'w-20' },
		{ key: 'reorder_level', label: 'Low stock alert', kind: 'number', width: 'w-24' }
	];
</script>

<svelte:head><title>Bulk Update Items · Accounting</title></svelte:head>

<PageHeader title="Bulk Update Items" description="Change prices and details of many items at once, or add new items from Excel." back={{ href: '/products', label: 'Products' }} />

<BulkGrid path="products" {columns} noun="item" matchKey="sku_code" />
