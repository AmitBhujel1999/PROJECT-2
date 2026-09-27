<script lang="ts">
	import { cn } from '$lib/utils';

	let { tabs, value = $bindable() }: { tabs: { value: string; label: string }[]; value: string } = $props();

	function onKey(e: KeyboardEvent, i: number) {
		if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
		const next = (i + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
		value = tabs[next].value;
		(document.getElementById(`tab-${tabs[next].value}`) as HTMLElement | null)?.focus();
	}
</script>

<div role="tablist" class="no-print mb-4 flex gap-1 overflow-x-auto border-b">
	{#each tabs as tab, i (tab.value)}
		<button
			id="tab-{tab.value}"
			role="tab"
			aria-selected={value === tab.value}
			tabindex={value === tab.value ? 0 : -1}
			onclick={() => (value = tab.value)}
			onkeydown={(e) => onKey(e, i)}
			class={cn(
				'-mb-px whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium transition-colors',
				value === tab.value ? 'border-primary text-primary' : 'border-transparent text-muted-foreground hover:text-foreground'
			)}
		>
			{tab.label}
		</button>
	{/each}
</div>
