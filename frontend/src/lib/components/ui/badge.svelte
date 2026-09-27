<script lang="ts">
	import type { Snippet } from 'svelte';
	import { cn } from '$lib/utils';

	type Variant = 'default' | 'success' | 'warning' | 'danger' | 'muted' | 'info';
	let { variant = 'default', class: className, children }: { variant?: Variant; class?: string; children?: Snippet } = $props();

	const styles: Record<Variant, string> = {
		default: 'bg-primary/10 text-primary ring-primary/20',
		success: 'bg-emerald-50 text-emerald-700 ring-emerald-600/20',
		warning: 'bg-amber-50 text-amber-800 ring-amber-600/25',
		danger: 'bg-red-50 text-red-700 ring-red-600/20',
		muted: 'bg-muted text-muted-foreground ring-border',
		info: 'bg-sky-50 text-sky-700 ring-sky-600/20'
	};
</script>

<span class={cn('inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs font-medium ring-1 ring-inset whitespace-nowrap', styles[variant], className)}>
	{@render children?.()}
</span>
