<script lang="ts">
	import { Search } from '@lucide/svelte';
	import { debounce } from '$lib/utilities/debounce';
	import { cn } from '$lib/utils';

	let {
		value = $bindable(''),
		placeholder = 'Search…',
		onSearch,
		class: className,
		id
	}: { value?: string; placeholder?: string; onSearch: (v: string) => void; class?: string; id?: string } = $props();

	const fire = debounce((v: string) => onSearch(v), 300);
</script>

<div class={cn('relative w-full sm:w-64', className)}>
	<Search class="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
	<input
		{id}
		type="search"
		bind:value
		oninput={(e) => fire(e.currentTarget.value)}
		{placeholder}
		aria-label={placeholder}
		class="h-9 w-full rounded-md border border-input bg-background pl-8 pr-3 text-sm shadow-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/60"
	/>
</div>
