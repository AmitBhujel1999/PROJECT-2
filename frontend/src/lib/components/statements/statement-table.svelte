<script lang="ts" module>
	export interface Line {
		label: string;
		/** Amount in the inner column (items). */
		amount?: string | null;
		/** Amount in the outer column (subtotals). */
		total?: string | null;
		indent?: boolean;
		strong?: boolean;
		/** Grand lines get a top border and a tinted background. */
		grand?: boolean;
		href?: string | null;
	}
</script>

<script lang="ts">
	import { money } from '$lib/utilities/format';
	import { cn } from '$lib/utils';

	let { lines, testid }: { lines: Line[]; testid?: string } = $props();

	/** Negative amounts are shown in brackets, as on printed statements. */
	function show(v: string | null | undefined): string {
		if (v === null || v === undefined || v === '') return '';
		return v.startsWith('-') ? `(${money(v.slice(1))})` : money(v);
	}
</script>

<table class="table-base statement" data-testid={testid}>
	<thead><tr><th>Particulars</th><th class="num w-40">Amount</th><th class="num w-40">Total</th></tr></thead>
	<tbody>
		{#each lines as l, i (i)}
			<tr class={cn(l.grand && 'border-t-2 bg-muted/50', l.strong && 'font-semibold')}>
				<td class={cn(l.indent && 'pl-8')}>
					{#if l.href}<a class="text-primary hover:underline" href={l.href}>{l.label}</a>{:else}{l.label}{/if}
				</td>
				<td class="num tabular-nums">{show(l.amount)}</td>
				<td class={cn('num tabular-nums', l.total?.startsWith('-') && 'text-destructive')}>{show(l.total)}</td>
			</tr>
		{/each}
	</tbody>
</table>
