<script lang="ts">
	import { AlertDialog } from 'bits-ui';
	import Button from './button.svelte';
	import Textarea from './textarea.svelte';

	let {
		open = $bindable(false),
		title,
		message,
		confirmLabel = 'Confirm',
		destructive = false,
		requireReason = false,
		reasonLabel = 'Reason',
		onConfirm
	}: {
		open?: boolean;
		title: string;
		message: string;
		confirmLabel?: string;
		destructive?: boolean;
		requireReason?: boolean;
		reasonLabel?: string;
		onConfirm: (reason: string) => Promise<unknown> | unknown;
	} = $props();

	let reason = $state('');
	let busy = $state(false);

	async function confirm() {
		if (requireReason && !reason.trim()) return;
		busy = true;
		try {
			await onConfirm(reason.trim());
			open = false;
			reason = '';
		} finally {
			busy = false;
		}
	}
</script>

<AlertDialog.Root bind:open>
	<AlertDialog.Portal>
		<AlertDialog.Overlay class="fixed inset-0 z-50 bg-black/40" />
		<AlertDialog.Content class="fixed left-1/2 top-1/2 z-50 w-[calc(100vw-1.5rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-xl border bg-background p-5 shadow-xl">
			<AlertDialog.Title class="text-base font-semibold">{title}</AlertDialog.Title>
			<AlertDialog.Description class="mt-2 text-sm text-muted-foreground">{message}</AlertDialog.Description>
			{#if requireReason}
				<div class="mt-4 grid gap-1.5">
					<label for="confirm-reason" class="text-sm font-medium">{reasonLabel} <span class="text-destructive">*</span></label>
					<Textarea id="confirm-reason" bind:value={reason} rows={2} maxlength={255} />
				</div>
			{/if}
			<div class="mt-5 flex justify-end gap-2">
				<AlertDialog.Cancel>
					{#snippet child({ props })}
						<Button variant="outline" {...props}>Cancel</Button>
					{/snippet}
				</AlertDialog.Cancel>
				<Button variant={destructive ? 'destructive' : 'default'} loading={busy} disabled={requireReason && !reason.trim()} onclick={confirm}>
					{confirmLabel}
				</Button>
			</div>
		</AlertDialog.Content>
	</AlertDialog.Portal>
</AlertDialog.Root>
