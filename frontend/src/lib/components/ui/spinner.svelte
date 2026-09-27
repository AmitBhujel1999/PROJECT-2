<script lang="ts">
	import { cn } from '$lib/utils';
	let { class: className = '' }: { class?: string } = $props();
</script>

<svg class={cn('size-4 animate-spin', className)} viewBox="0 0 24 24" fill="none" aria-hidden="true">
	<circle cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4" class="opacity-25" />
	<path d="M4 12a8 8 0 0 1 8-8" stroke="currentColor" stroke-width="4" stroke-linecap="round" class="opacity-75" />
</svg>
