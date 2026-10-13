<script lang="ts">
	import { onMount } from 'svelte';
	import { auth } from '$lib/stores/auth.svelte';
	import { accounts } from '$lib/stores/accounts.svelte';
	import { cn } from '$lib/utils';
	import AccountFormDialog from './account-form-dialog.svelte';

	/**
	 * Picks the cash/bank account money came into or went out of.
	 * With `placeholder` it is a filter ("" = any account); otherwise it starts
	 * on the first cash account. Managers get "+ Add new account…" at the end.
	 */
	let {
		value = $bindable<number | string | null>(null),
		id,
		placeholder,
		class: className,
		onchange,
		'aria-label': ariaLabel
	}: {
		value?: number | string | null;
		id?: string;
		placeholder?: string;
		class?: string;
		onchange?: () => void;
		'aria-label'?: string;
	} = $props();

	const ADD = '__add__';
	let addOpen = $state(false);
	const canAdd = $derived(!placeholder && auth.can('cashbank.manage'));
	const options = $derived(placeholder ? accounts.all : accounts.active);

	onMount(() => {
		accounts.ensure().then(() => {
			if (!placeholder && !value && accounts.fallback) value = accounts.fallback.id;
		});
	});

	function changed(e: Event & { currentTarget: HTMLSelectElement }) {
		const raw = e.currentTarget.value;
		if (raw === ADD) {
			e.currentTarget.value = value ? String(value) : '';
			addOpen = true;
			return;
		}
		value = placeholder ? raw : Number(raw);
		onchange?.();
	}
</script>

<select
	{id}
	value={value ? String(value) : ''}
	onchange={changed}
	aria-label={ariaLabel}
	class={cn(
		'flex h-9 w-full rounded-md border border-input bg-background px-2.5 py-1 text-sm shadow-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/60 disabled:opacity-60',
		className
	)}
>
	{#if placeholder !== undefined}<option value="">{placeholder}</option>{:else if !accounts.loaded}<option value="">Loading…</option>{/if}
	{#each options as a (a.id)}
		<option value={String(a.id)}>{a.name}{a.account_number ? ` (${a.account_number})` : ''}{a.is_active ? '' : ' (inactive)'}</option>
	{/each}
	{#if canAdd}<option value={ADD}>+ Add new account…</option>{/if}
</select>

{#if canAdd}
	<AccountFormDialog
		bind:open={addOpen}
		onSaved={(a) => {
			value = a.id;
			onchange?.();
		}}
	/>
{/if}
