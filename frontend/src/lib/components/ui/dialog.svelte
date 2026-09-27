<script lang="ts">
	import { Dialog } from 'bits-ui';
	import type { Snippet } from 'svelte';
	import { X } from '@lucide/svelte';
	import { cn } from '$lib/utils';

	let {
		open = $bindable(false),
		title,
		description,
		class: className,
		children,
		footer
	}: { open?: boolean; title: string; description?: string; class?: string; children?: Snippet; footer?: Snippet } = $props();
</script>

<Dialog.Root bind:open>
	<Dialog.Portal>
		<Dialog.Overlay class="fixed inset-0 z-50 bg-black/40 backdrop-blur-[1px]" />
		<Dialog.Content
			class={cn(
				'fixed left-1/2 top-1/2 z-50 flex max-h-[92vh] w-[calc(100vw-1.5rem)] max-w-lg -translate-x-1/2 -translate-y-1/2 flex-col rounded-xl border bg-background shadow-xl focus:outline-none',
				className
			)}
		>
			<div class="flex items-start justify-between gap-4 border-b px-5 py-4">
				<div>
					<Dialog.Title class="text-base font-semibold">{title}</Dialog.Title>
					{#if description}<Dialog.Description class="mt-0.5 text-sm text-muted-foreground">{description}</Dialog.Description>{/if}
				</div>
				<Dialog.Close class="rounded-md p-1 text-muted-foreground hover:bg-muted hover:text-foreground" aria-label="Close">
					<X class="size-4" />
				</Dialog.Close>
			</div>
			<div class="overflow-y-auto px-5 py-4">{@render children?.()}</div>
			{#if footer}<div class="flex flex-wrap justify-end gap-2 border-t px-5 py-3">{@render footer()}</div>{/if}
		</Dialog.Content>
	</Dialog.Portal>
</Dialog.Root>
