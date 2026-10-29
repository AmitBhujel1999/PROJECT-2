<script lang="ts">
	import { FileDown, Printer } from '@lucide/svelte';
	import Dialog from './dialog.svelte';
	import Button from './button.svelte';
	import Spinner from './spinner.svelte';

	// Shows a PDF inside the app. Pages are drawn with pdf.js, so it also works in the
	// Android app, whose WebView cannot show PDFs by itself.
	let {
		open = $bindable(false),
		url,
		title,
		download,
		onprint
	}: {
		open?: boolean;
		/** PDF to show (for invoices, a preview URL that is not counted as a print). */
		url: string;
		title: string;
		/** URL of the real file for the Download button. */
		download?: string;
		onprint?: () => void;
	} = $props();

	let pages: HTMLDivElement | null = $state(null);
	let loading = $state(false);
	let error = $state('');

	$effect(() => {
		const target = pages;
		if (!open || !target) return;
		const src = url;
		let cancelled = false;
		loading = true;
		error = '';
		target.replaceChildren();
		(async () => {
			try {
				const [pdfjs, worker] = await Promise.all([import('pdfjs-dist'), import('pdfjs-dist/build/pdf.worker.min.mjs?url')]);
				pdfjs.GlobalWorkerOptions.workerSrc = worker.default;
				const res = await fetch(src, { credentials: 'same-origin' });
				if (!res.ok) throw new Error(`Could not load the PDF (error ${res.status}).`);
				const doc = await pdfjs.getDocument({ data: new Uint8Array(await res.arrayBuffer()), isEvalSupported: false }).promise;
				const width = target.clientWidth || 800;
				const ratio = window.devicePixelRatio || 1;
				for (let n = 1; n <= doc.numPages && !cancelled; n++) {
					const page = await doc.getPage(n);
					const viewport = page.getViewport({ scale: (width / page.getViewport({ scale: 1 }).width) * ratio });
					const canvas = document.createElement('canvas');
					canvas.width = viewport.width;
					canvas.height = viewport.height;
					canvas.className = 'w-full rounded border bg-white shadow-sm';
					target.append(canvas);
					await page.render({ canvasContext: canvas.getContext('2d')!, viewport }).promise;
				}
				await doc.destroy();
			} catch (e) {
				if (!cancelled) error = e instanceof Error ? e.message : String(e);
			} finally {
				if (!cancelled) loading = false;
			}
		})();
		return () => {
			cancelled = true;
		};
	});
</script>

<Dialog bind:open {title} class="max-w-4xl">
	{#if loading}
		<div class="flex items-center justify-center gap-2 py-10 text-sm text-muted-foreground"><Spinner />Loading preview…</div>
	{/if}
	{#if error}
		<p class="py-6 text-center text-sm text-destructive">{error}</p>
	{/if}
	<div bind:this={pages} class="space-y-3" data-testid="pdf-preview"></div>
	{#snippet footer()}
		{#if download}
			<Button variant="outline" href={download} download><FileDown />Download</Button>
		{/if}
		{#if onprint}
			<Button
				onclick={() => {
					open = false;
					onprint();
				}}><Printer />Print</Button
			>
		{/if}
	{/snippet}
</Dialog>
