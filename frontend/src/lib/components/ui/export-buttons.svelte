<script lang="ts">
	import { FileSpreadsheet, FileText, Printer } from '@lucide/svelte';
	import { exportUrl, type Query } from '$lib/api/client';
	import Button from './button.svelte';

	let { path, query = {}, print = true }: { path: string; query?: Query; print?: boolean } = $props();
</script>

<Button variant="outline" size="sm" href={exportUrl(path, query, 'csv')} download data-testid="export-csv"><FileSpreadsheet />CSV</Button>
<Button variant="outline" size="sm" href={exportUrl(path, query, 'pdf')} download data-testid="export-pdf"><FileText />PDF</Button>
{#if print}
	<Button variant="outline" size="sm" onclick={() => window.print()} data-testid="print"><Printer />Print</Button>
{/if}
