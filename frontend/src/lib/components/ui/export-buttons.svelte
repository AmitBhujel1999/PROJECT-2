<script lang="ts">
	import { Eye, FileSpreadsheet, FileText, Printer } from '@lucide/svelte';
	import { exportUrl, type Query } from '$lib/api/client';
	import Button from './button.svelte';
	import PdfPreview from './pdf-preview.svelte';

	let { path, query = {}, print = true, title = 'Preview' }: { path: string; query?: Query; print?: boolean; title?: string } = $props();
	let previewOpen = $state(false);
</script>

<Button variant="outline" size="sm" href={exportUrl(path, query, 'csv')} download data-testid="export-csv"><FileSpreadsheet />CSV</Button>
<Button variant="outline" size="sm" onclick={() => (previewOpen = true)} data-testid="export-preview"><Eye />Preview</Button>
<PdfPreview bind:open={previewOpen} {title} url={exportUrl(path, query, 'pdf', true)} download={exportUrl(path, query, 'pdf')} />
<Button variant="outline" size="sm" href={exportUrl(path, query, 'pdf')} download data-testid="export-pdf"><FileText />PDF</Button>
{#if print}
	<Button variant="outline" size="sm" onclick={() => window.print()} data-testid="print"><Printer />Print</Button>
{/if}
