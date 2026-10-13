<script lang="ts">
	import { goto } from '$app/navigation';
	import { Plus, Save, Printer, Trash2, CircleCheck, CircleAlert } from '@lucide/svelte';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import { money, today, toCents, fromCents } from '$lib/utilities/format';
	import type { JournalVoucher } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import AccountPicker from '$lib/components/journal/account-picker.svelte';

	type Line = { uid: number; key: string; label: string; debit: string; credit: string; narration: string };
	let uid = 0;
	const blank = (): Line => ({ uid: ++uid, key: '', label: '', debit: '', credit: '', narration: '' });

	let date = $state(today());
	let narration = $state('');
	let lines = $state<Line[]>([blank(), blank()]);
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);

	const totalDr = $derived(lines.reduce((s, l) => s + toCents(l.debit), 0n));
	const totalCr = $derived(lines.reduce((s, l) => s + toCents(l.credit), 0n));
	const diff = $derived(totalDr - totalCr);
	const balanced = $derived(totalDr > 0n && diff === 0n);

	/** Typing an amount on one side clears the other side of that line. */
	function setSide(line: Line, side: 'debit' | 'credit') {
		if (toCents(line[side]) > 0n) line[side === 'debit' ? 'credit' : 'debit'] = '';
	}

	/** Put the remaining difference on a new line so the voucher balances. */
	function addLine() {
		const l = blank();
		if (diff > 0n) l.credit = fromCents(diff);
		if (diff < 0n) l.debit = fromCents(-diff);
		lines.push(l);
	}

	async function save(print: boolean) {
		errors = {};
		const used = lines.filter((l) => l.key || toCents(l.debit) > 0n || toCents(l.credit) > 0n);
		if (used.some((l) => !l.key)) {
			errors.lines = 'Choose an account on every line that has an amount.';
			return;
		}
		if (!balanced) {
			errors.lines = 'Debit and credit totals must be equal.';
			return;
		}
		busy = true;
		try {
			const res = await api.post<JournalVoucher>('journal/vouchers/', {
				date,
				narration,
				lines: used.map((l) => ({ account: l.key, debit: l.debit || '0', credit: l.credit || '0', narration: l.narration }))
			});
			toast.success(`✓ ${res.message}`);
			if (print) window.open(`/api/journal/vouchers/${res.data.id}/pdf/?inline=1`, '_blank');
			goto(`/journal/${res.data.id}`);
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError) errors.lines ??= err.message;
		} finally {
			busy = false;
		}
	}

	function onKeydown(e: KeyboardEvent) {
		if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
			e.preventDefault();
			save(false);
		}
	}
</script>

<svelte:window onkeydown={onKeydown} />
<svelte:head><title>New journal voucher · Accounting</title></svelte:head>

<PageHeader title="New journal voucher" description="Debit and credit must be equal. Number is assigned on save. Ctrl+S saves." back={{ href: '/journal', label: 'Journal vouchers' }} />

<form class="grid gap-4" onsubmit={(e) => (e.preventDefault(), save(false))}>
	<Card>
		<div class="grid gap-4 sm:grid-cols-4">
			<Field label="Date" for="jv-date" required error={errors.date}><Input id="jv-date" type="date" bind:value={date} required /></Field>
			<Field label="Narration" for="jv-narration" class="sm:col-span-3">
				<Input id="jv-narration" bind:value={narration} maxlength={2000} placeholder="e.g. Owner brought furniture into the business" />
			</Field>
		</div>
	</Card>

	<Card title="Entries" bodyClass="p-0">
		<div class="overflow-x-auto">
			<table class="table-base" data-testid="jv-lines">
				<thead>
					<tr><th class="w-8">#</th><th class="min-w-64">Account</th><th class="num w-40">Debit</th><th class="num w-40">Credit</th><th class="min-w-48">Line note</th><th class="w-10"></th></tr>
				</thead>
				<tbody>
					{#each lines as l, i (l.uid)}
						<tr>
							<td class="text-muted-foreground">{i + 1}</td>
							<td><AccountPicker id="jv-acc-{i}" bind:key={l.key} bind:label={l.label} invalid={!!errors.lines && !l.key && (!!l.debit || !!l.credit)} /></td>
							<td><Input type="number" min="0" step="0.01" class="text-right" bind:value={l.debit} oninput={() => setSide(l, 'debit')} aria-label="Debit line {i + 1}" /></td>
							<td><Input type="number" min="0" step="0.01" class="text-right" bind:value={l.credit} oninput={() => setSide(l, 'credit')} aria-label="Credit line {i + 1}" /></td>
							<td><Input bind:value={l.narration} maxlength={255} aria-label="Note line {i + 1}" /></td>
							<td>
								{#if lines.length > 2}
									<Button size="sm" variant="ghost" onclick={() => lines.splice(i, 1)} aria-label="Remove line {i + 1}"><Trash2 /></Button>
								{/if}
							</td>
						</tr>
					{/each}
				</tbody>
				<tfoot>
					<tr class="bg-muted/40 font-semibold">
						<td></td>
						<td><Button size="sm" variant="outline" onclick={addLine} data-testid="jv-add-line"><Plus />Add line</Button></td>
						<td class="num" data-testid="jv-total-dr">{money(fromCents(totalDr))}</td>
						<td class="num" data-testid="jv-total-cr">{money(fromCents(totalCr))}</td>
						<td colspan="2">
							{#if balanced}
								<span class="inline-flex items-center gap-1 text-emerald-700"><CircleCheck class="size-4" />Balanced</span>
							{:else if totalDr || totalCr}
								<span class="inline-flex items-center gap-1 text-amber-700"><CircleAlert class="size-4" />Difference {money(fromCents(diff < 0n ? -diff : diff))} {diff > 0n ? 'Dr' : 'Cr'}</span>
							{/if}
						</td>
					</tr>
				</tfoot>
			</table>
		</div>
		{#if errors.lines}<p class="px-3 py-2 text-sm text-destructive" role="alert">{errors.lines}</p>{/if}
	</Card>

	<div class="flex flex-wrap justify-end gap-2">
		<Button variant="outline" size="lg" loading={busy} onclick={() => save(true)}><Printer />Save & Print</Button>
		<Button type="submit" size="lg" loading={busy} disabled={!balanced} data-testid="save-jv"><Save />Save voucher ({auth.currency} {money(fromCents(totalDr))})</Button>
	</div>
</form>
