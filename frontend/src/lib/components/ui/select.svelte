<script lang="ts">
	import type { HTMLSelectAttributes } from 'svelte/elements';
	import { cn } from '$lib/utils';

	type Option = { value: string | number; label: string; disabled?: boolean };
	let {
		value = $bindable(),
		options = [],
		placeholder,
		class: className,
		children,
		...rest
	}: HTMLSelectAttributes & { options?: Option[]; placeholder?: string } = $props();
</script>

<select
	bind:value
	class={cn(
		'flex h-9 w-full rounded-md border border-input bg-background px-2.5 py-1 text-sm shadow-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/60 disabled:opacity-60 aria-[invalid=true]:border-destructive',
		className
	)}
	{...rest}
>
	{#if placeholder !== undefined}<option value="">{placeholder}</option>{/if}
	{#each options as opt (opt.value)}
		<option value={opt.value} disabled={opt.disabled}>{opt.label}</option>
	{/each}
	{@render children?.()}
</select>
