<script lang="ts">
	/** Single-choice filter chips (phone replacement for a filter select). */
	let {
		value = $bindable(''),
		options,
		onchange
	}: { value?: string; options: { value: string; label: string }[]; onchange?: (v: string) => void } = $props();
</script>

<div class="-mx-1 flex gap-1.5 overflow-x-auto px-1 pb-0.5" role="radiogroup">
	{#each options as o (o.value)}
		<button type="button" role="radio" aria-checked={value === o.value} class="m-chip" class:on={value === o.value}
			onclick={() => {
				value = o.value;
				onchange?.(o.value);
			}}>{o.label}</button>
	{/each}
</div>
