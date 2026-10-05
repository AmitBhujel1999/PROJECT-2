<script lang="ts">
	import Self from './menu-tree.svelte';
	import { isGroup, type MenuLeaf, type MenuNode } from './menu';

	let {
		nodes,
		nested = false,
		expandAll = false,
		onPick
	}: { nodes: MenuNode[]; nested?: boolean; expandAll?: boolean; onPick: (leaf: MenuLeaf) => void } = $props();

	// svelte-ignore state_referenced_locally
	let open = $state<Record<string, boolean>>(Object.fromEntries(nodes.filter(isGroup).map((g) => [g.label, expandAll || !!g.open])));
</script>

<ul class={nested ? 'menu-tree-nested' : 'menu-tree'} role={nested ? 'group' : undefined}>
	{#each nodes as node, i (typeof node === 'string' ? `sep-${i}` : node.label)}
		{#if node === 'separator'}
			<li class="menu-sep" aria-hidden="true"></li>
		{:else if isGroup(node)}
			<li>
				<button type="button" class="menu-group" class:open={open[node.label]} aria-expanded={!!open[node.label]}
					onclick={() => (open[node.label] = !open[node.label])}>{node.label}</button>
				{#if open[node.label]}
					<Self nodes={node.children} nested {expandAll} {onPick} />
				{/if}
			</li>
		{:else}
			<li>
				{#if node.href}
					<a class="menu-leaf" href={node.href} onclick={() => onPick(node)}>{node.label}</a>
				{:else}
					<button type="button" class="menu-leaf" onclick={() => onPick(node)}>{node.label}</button>
				{/if}
			</li>
		{/if}
	{/each}
</ul>
