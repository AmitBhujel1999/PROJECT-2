<script lang="ts">
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';

	const keys = [
		['Alt + C / A / T / D / R / H', 'Open the Company, Administration, Transactions, Display, Reports or Help menu'],
		['Esc', 'Close the open menu; otherwise go back (or to Home)'],
		['F2', 'Add a new record on list pages (new sale, new expense, …)'],
		['F5', 'Print the current page'],
		['Ctrl + K', 'Global search: products, parties, invoices, bills, receipts, payments, expenses'],
		['Tab / Shift + Tab', 'Move between fields and menu entries']
	];
</script>

<svelte:head><title>Keyboard Shortcuts · Accounting</title></svelte:head>

<PageHeader title="Keyboard Shortcuts" description="Work without the mouse." back={{ href: '/', label: 'Home' }} />

<Card bodyClass="p-0">
	<table class="table-base">
		<thead><tr><th class="w-64">Keys</th><th>Action</th></tr></thead>
		<tbody>
			{#each keys as [k, action] (k)}
				<tr><td class="font-mono text-xs font-semibold">{k}</td><td>{action}</td></tr>
			{/each}
		</tbody>
	</table>
</Card>
