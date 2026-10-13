<script lang="ts">
	import { Dialog } from 'bits-ui';
	import { api, ApiError } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import type { BusinessSettings } from '$lib/types';
	import { today } from '$lib/utilities/format';
	import { adToBs } from '$lib/utilities/nepali-date';
	import { cn } from '$lib/utils';
	import Button from '$lib/components/ui/button.svelte';

	/**
	 * Asked once per company: the first time someone who can manage settings
	 * logs in and no date system has been chosen yet.
	 */
	let choice = $state<'BS' | 'AD'>('BS');
	let busy = $state(false);

	const pad = (n: number) => String(n).padStart(2, '0');
	const iso = today();
	const bs = adToBs(iso);
	const samples = {
		BS: `${bs.year}/${pad(bs.month)}/${pad(bs.day)}`,
		AD: iso.split('-').reverse().join('/')
	};
	const options = [
		{ value: 'BS' as const, title: 'Nepali date (B.S.)', hint: 'Bikram Sambat calendar, e.g. ' + samples.BS },
		{ value: 'AD' as const, title: 'English date (A.D.)', hint: 'Gregorian calendar, e.g. ' + samples.AD }
	];

	async function save() {
		busy = true;
		try {
			const res = await api.put<BusinessSettings>('settings/', { date_system: choice });
			auth.settings = res.data;
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Could not save the date setting.');
		} finally {
			busy = false;
		}
	}
</script>

<Dialog.Root open>
	<Dialog.Portal>
		<Dialog.Overlay class="fixed inset-0 z-50 bg-black/40 backdrop-blur-[1px]" />
		<Dialog.Content
			class="fixed left-1/2 top-1/2 z-50 w-[calc(100vw-1.5rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl border bg-background shadow-xl focus:outline-none"
			escapeKeydownBehavior="ignore"
			interactOutsideBehavior="ignore"
			data-testid="date-system-dialog"
		>
			<div class="border-b px-5 py-4">
				<Dialog.Title class="text-base font-semibold">Choose date system</Dialog.Title>
				<Dialog.Description class="mt-0.5 text-sm text-muted-foreground">
					How should dates be shown and entered for {auth.settings?.business_name ?? 'this company'}?
				</Dialog.Description>
			</div>
			<div class="grid gap-2 px-5 py-4" role="radiogroup" aria-label="Date system">
				{#each options as opt (opt.value)}
					<label
						class={cn(
							'flex cursor-pointer items-start gap-3 rounded-lg border p-3 transition-colors hover:bg-muted/60',
							choice === opt.value && 'border-primary bg-primary/5 ring-1 ring-primary'
						)}
					>
						<input type="radio" name="date-system" value={opt.value} bind:group={choice} class="mt-1 accent-[var(--color-primary)]" />
						<span>
							<span class="block font-medium">{opt.title}</span>
							<span class="block text-sm text-muted-foreground">{opt.hint}</span>
						</span>
					</label>
				{/each}
				<p class="text-xs text-muted-foreground">You can change this later in Settings &rsaquo; Business Settings.</p>
			</div>
			<div class="flex justify-end border-t px-5 py-3">
				<Button onclick={save} loading={busy}>Continue</Button>
			</div>
		</Dialog.Content>
	</Dialog.Portal>
</Dialog.Root>
