<script lang="ts" module>
	export interface BulkColumn {
		key: string;
		label: string;
		kind?: 'text' | 'number' | 'select' | 'bool';
		options?: { value: string; label: string }[];
		/** Tailwind width class for the column. */
		width?: string;
		/** Needed on a new row before it is saved. */
		required?: boolean;
	}
</script>

<script lang="ts">
	import { Clipboard, ClipboardCheck, Plus, Save, Trash2, CircleAlert, FileDown, FileUp } from '@lucide/svelte';
	import { beforeNavigate } from '$app/navigation';
	import { api, ApiError, buildUrl } from '$lib/api/client';
	import { toast } from '$lib/stores/toast.svelte';
	import type { Paginated } from '$lib/types';
	import Button from '$lib/components/ui/button.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import { cn } from '$lib/utils';

	let {
		path,
		columns,
		noun,
		matchKey = 'name'
	}: {
		/** API collection, e.g. "products" (lists from it, saves to <path>/bulk/). */
		path: string;
		columns: BulkColumn[];
		noun: string;
		/** Column that finds a record already in the app when a file is uploaded (e.g. item code). */
		matchKey?: string;
	} = $props();

	type Values = Record<string, string | boolean>;
	interface Row {
		key: number;
		id: number | null;
		values: Values;
		orig: Values | null;
	}

	let rows = $state<Row[]>([]);
	let errors = $state<Record<number, Record<string, string>>>({});
	let loading = $state(true);
	let busy = $state(false);
	let loadError = $state<string | null>(null);
	let search = $state('');
	let show = $state<'active' | 'inactive' | 'all'>('active');
	let copied = $state(false);
	let nextKey = 0;

	const ALL_COLUMNS = $derived<BulkColumn[]>([...columns, { key: 'is_active', label: 'In use', kind: 'bool', width: 'w-16' }]);

	function fromApi(item: Record<string, unknown>): Values {
		const v: Values = {};
		for (const c of ALL_COLUMNS) {
			const raw = item[c.key];
			v[c.key] = c.kind === 'bool' ? !!raw : c.kind === 'number' ? String(Number(raw ?? 0)) : String(raw ?? '');
		}
		return v;
	}

	function blank(): Row {
		const values: Values = {};
		for (const c of ALL_COLUMNS) values[c.key] = c.kind === 'bool' ? true : c.kind === 'select' ? (c.options?.[0]?.value ?? '') : '';
		return { key: nextKey++, id: null, values, orig: null };
	}

	async function load() {
		loading = true;
		loadError = null;
		try {
			const items: Record<string, unknown>[] = [];
			for (let page = 1; ; page++) {
				const res = await api.get<Paginated<Record<string, unknown>>>(`${path}/`, { page, page_size: 100, ordering: 'name' });
				items.push(...res.results);
				if (page >= res.total_pages) break;
			}
			rows = [...items.map((it) => ({ key: nextKey++, id: it.id as number, values: fromApi(it), orig: fromApi(it) })), blank(), blank(), blank()];
			errors = {};
		} catch (err) {
			loadError = err instanceof ApiError ? err.message : `Could not load ${noun}s.`;
		} finally {
			loading = false;
		}
	}
	load();

	const isBlank = (r: Row) => r.id === null && columns.every((c) => c.kind === 'select' || c.kind === 'bool' || !String(r.values[c.key]).trim());
	function changedKeys(r: Row): string[] {
		if (!r.orig) return [];
		return ALL_COLUMNS.filter((c) => (c.kind === 'number' ? Number(r.values[c.key]) !== Number(r.orig![c.key]) : r.values[c.key] !== r.orig![c.key])).map((c) => c.key);
	}
	const isChanged = (r: Row) => (r.id === null ? !isBlank(r) : changedKeys(r).length > 0);
	const pending = $derived(rows.filter(isChanged));

	const visible = $derived.by(() => {
		const q = search.trim().toLowerCase();
		return rows.filter((r) => {
			if (r.id === null) return true; // new rows always show
			if (show === 'active' && !r.values.is_active) return false;
			if (show === 'inactive' && r.values.is_active) return false;
			return !q || columns.some((c) => String(r.values[c.key]).toLowerCase().includes(q));
		});
	});

	// ---------------------------------------------------------------- paste
	/** Turn pasted text (e.g. "30 days", "kg", "yes") into the column's value. */
	function normalise(c: BulkColumn, text: string): string | boolean {
		const t = text.trim();
		if (c.kind === 'bool') return !/^(no|n|false|0|inactive|off)$/i.test(t);
		if (c.kind === 'number') return t.replace(/[,\s]/g, '').replace(/^\((.*)\)$/, '-$1');
		if (c.kind === 'select' && c.options) {
			const low = t.toLowerCase();
			const hit =
				c.options.find((o) => o.value.toLowerCase() === low || o.label.toLowerCase() === low) ??
				c.options.find((o) => /\d/.test(low) && o.label.toLowerCase().startsWith(low.replace(/\D+$/, '')));
			return hit ? hit.value : t;
		}
		return t;
	}

	function onPaste(e: ClipboardEvent, rowIndex: number, colIndex: number) {
		const text = e.clipboardData?.getData('text/plain') ?? '';
		if (!/[\t\n]/.test(text.replace(/\r?\n$/, ''))) return; // a single value: let the input handle it
		e.preventDefault();
		const lines = text.replace(/\r/g, '').replace(/\n$/, '').split('\n').map((l) => l.split('\t'));
		// Skip a heading line (e.g. one copied with "Copy to Excel").
		const labels = ALL_COLUMNS.slice(colIndex).map((c) => c.label.toLowerCase());
		if (lines[0].every((cell, j) => cell.trim().toLowerCase() === labels[j])) lines.shift();
		if (!lines.length) return;
		const target = [...visible];
		lines.forEach((cells, i) => {
			let row = target[rowIndex + i];
			if (!row) {
				row = blank();
				rows.push(row);
				row = rows[rows.length - 1];
				target.push(row);
			}
			cells.forEach((cell, j) => {
				const c = ALL_COLUMNS[colIndex + j];
				if (c) row.values[c.key] = normalise(c, cell);
			});
		});
		if (!rows.slice(-1).some(isBlank)) rows.push(blank());
		toast.success(`Pasted ${lines.length} ${lines.length === 1 ? 'row' : 'rows'}. Check them, then press Save.`);
	}

	async function copyAll() {
		const label = (c: BulkColumn, v: string | boolean) =>
			c.kind === 'bool' ? (v ? 'Yes' : 'No') : c.kind === 'select' ? (c.options?.find((o) => o.value === v)?.label ?? String(v)) : String(v).replace(/[\t\n]/g, ' ');
		const tsv = [
			ALL_COLUMNS.map((c) => c.label).join('\t'),
			...visible.filter((r) => !isBlank(r)).map((r) => ALL_COLUMNS.map((c) => label(c, r.values[c.key])).join('\t'))
		].join('\n');
		try {
			await navigator.clipboard.writeText(tsv);
			copied = true;
			setTimeout(() => (copied = false), 1500);
			toast.success('Copied. Now paste it in Excel or Google Sheets.');
		} catch {
			toast.error('Could not copy. Your browser did not allow it.');
		}
	}

	// ------------------------------------------------------- Excel file
	let fileInput = $state<HTMLInputElement | null>(null);
	let uploading = $state(false);

	async function onFile(e: Event) {
		const input = e.currentTarget as HTMLInputElement;
		const file = input.files?.[0];
		input.value = '';
		if (!file) return;
		uploading = true;
		try {
			const form = new FormData();
			form.append('file', file);
			const res = await api.upload<{ rows: Record<string, string>[]; skipped_examples: number }>(`${path}/bulk/upload/`, form);
			applyUpload(res.data.rows, res.data.skipped_examples);
		} catch (err) {
			toast.error(err instanceof ApiError ? err.message : 'Could not read the file.');
		} finally {
			uploading = false;
		}
	}

	function applyUpload(uploaded: Record<string, string>[], skipped: number) {
		const key = (v: unknown) => String(v ?? '').trim().toLowerCase();
		const byMatch = new Map(rows.filter((r) => r.id !== null).map((r) => [key(r.values[matchKey]), r]));
		let added = 0;
		let matched = 0;
		for (const u of uploaded) {
			let row = u[matchKey] ? byMatch.get(key(u[matchKey])) : undefined;
			const isNew = !row;
			if (row) matched++;
			else {
				row = rows.find(isBlank);
				if (!row) {
					rows.push(blank());
					row = rows[rows.length - 1];
				}
				added++;
			}
			for (const [k, v] of Object.entries(u)) {
				const c = ALL_COLUMNS.find((col) => col.key === k);
				// An empty cell never wipes out what an existing record already has.
				if (c && (isNew || v !== '')) row.values[k] = v === '' && c.kind === 'bool' ? true : normalise(c, v);
			}
			if (u[matchKey]) byMatch.set(key(u[matchKey]), row);
		}
		if (!rows.slice(-1).some(isBlank)) rows.push(blank());
		search = '';
		show = 'all';
		const parts = [added && `${added} new`, matched && `${matched} already in the app`].filter(Boolean).join(', ');
		toast.success(`Loaded ${uploaded.length} ${uploaded.length === 1 ? 'row' : 'rows'} from the file (${parts}).${skipped ? ' Example rows were skipped.' : ''} Check them, then press Save.`);
	}

	// ----------------------------------------------------------- keyboard
	function onKey(e: KeyboardEvent, rowIndex: number, colIndex: number) {
		const move = e.key === 'Enter' || e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : 0;
		if (!move || (e.target as HTMLElement).tagName === 'SELECT') return;
		e.preventDefault();
		if (move > 0 && rowIndex === visible.length - 1) rows.push(blank());
		requestAnimationFrame(() => (document.querySelector(`[data-cell="${rowIndex + move}-${colIndex}"]`) as HTMLElement | null)?.focus());
	}

	function onWindowKey(e: KeyboardEvent) {
		if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
			e.preventDefault();
			save();
		}
	}

	// --------------------------------------------------------------- save
	async function save() {
		if (!pending.length || busy) return;
		for (const r of pending.filter((r) => r.id === null)) {
			const missing = columns.filter((c) => c.required && !String(r.values[c.key]).trim());
			if (missing.length) {
				errors = { [r.key]: Object.fromEntries(missing.map((c) => [c.key, 'Please fill this in.'])) };
				toast.error(`Please fill in ${missing.map((c) => c.label).join(', ')} for the new row.`);
				return;
			}
		}
		const sent = [...pending];
		const body = sent.map((r) => {
			// New rows leave out empty cells so the server's defaults apply.
			if (r.id === null) return Object.fromEntries(Object.entries(r.values).filter(([, v]) => v !== ''));
			return { id: r.id, ...Object.fromEntries(changedKeys(r).map((k) => [k, r.values[k]])) };
		});
		busy = true;
		errors = {};
		try {
			const res = await api.post<{ created: number; updated: number }>(`${path}/bulk/`, { rows: body });
			toast.success(res.message);
			await load();
		} catch (err) {
			if (err instanceof ApiError && err.code === 'BULK_INVALID') {
				const byIndex = ((err.details as { rows?: Record<string, Record<string, string>> })?.rows ?? {}) as Record<string, Record<string, string>>;
				errors = Object.fromEntries(Object.entries(byIndex).map(([i, e]) => [sent[Number(i)].key, e]));
			}
			toast.error(err instanceof ApiError ? err.message : 'Could not save. Please try again.');
		} finally {
			busy = false;
		}
	}

	function removeRow(r: Row) {
		rows = rows.filter((x) => x.key !== r.key);
	}

	beforeNavigate((nav) => {
		if (pending.length && !busy && !confirm('Your changes are not saved yet. Leave this page anyway?')) nav.cancel();
	});

	/** Server messages in everyday words. */
	function plain(msg: string): string {
		if (/valid number/i.test(msg)) return 'Enter a number.';
		if (/may not be blank|is required/i.test(msg)) return 'Please fill this in.';
		if (/greater than or equal to 0/i.test(msg)) return 'Cannot be less than 0.';
		if (/less than or equal to 100/i.test(msg)) return 'Cannot be more than 100.';
		if (/not a valid choice/i.test(msg)) return 'Pick one from the list.';
		if (/already exists/i.test(msg)) return 'Already used by another one.';
		if (/valid email/i.test(msg)) return 'This email address does not look right.';
		const long = msg.match(/no more than (\d+) characters/i);
		if (long) return `Too long (at most ${long[1]} letters).`;
		return msg;
	}

	const problems = $derived(
		rows
			.filter((r) => errors[r.key])
			.flatMap((r) =>
				Object.entries(errors[r.key]).map(([field, msg]) => {
					const col = ALL_COLUMNS.find((c) => c.key === field);
					const who = String(r.values.name ?? '').trim() || 'New row';
					return `${who}${col ? ` – ${col.label}` : ''}: ${plain(msg)}`;
				})
			)
	);
</script>

<svelte:window onkeydown={onWindowKey} />

<Card bodyClass="p-0" class="mb-3">
	<div class="flex flex-wrap items-center justify-between gap-2 p-3">
		<div class="flex flex-wrap items-center gap-2">
			<input class="h-8 w-56 rounded-md border border-input bg-background px-2.5 text-sm" placeholder="Find by name…" bind:value={search} aria-label="Find" />
			<Select bind:value={show} class="h-8 w-32" aria-label="Show"
				options={[{ value: 'active', label: 'In use' }, { value: 'inactive', label: 'Not in use' }, { value: 'all', label: 'Show all' }]} />
		</div>
		<div class="flex flex-wrap gap-2">
			<Button variant="outline" size="sm" href={buildUrl(`${path}/bulk/template/`)} download data-testid="bulk-format"><FileDown />Download format</Button>
			<Button variant="outline" size="sm" onclick={() => fileInput?.click()} loading={uploading} data-testid="bulk-upload"><FileUp />Upload Excel</Button>
			<input bind:this={fileInput} type="file" accept=".xlsx,.csv,.xls" class="hidden" onchange={onFile} data-testid="bulk-file" />
			<Button variant="outline" size="sm" onclick={copyAll} data-testid="bulk-copy">{#if copied}<ClipboardCheck />{:else}<Clipboard />{/if}Copy to Excel</Button>
			<Button variant="outline" size="sm" onclick={() => rows.push(blank(), blank(), blank(), blank(), blank())}><Plus />More rows</Button>
			<Button size="sm" onclick={save} loading={busy} disabled={!pending.length} data-testid="bulk-save"><Save />{pending.length ? `Save ${pending.length} ${pending.length === 1 ? 'change' : 'changes'}` : 'Save'}</Button>
		</div>
	</div>
	<p class="border-t px-3 py-2 text-xs text-muted-foreground">
		Type in the boxes, paste rows copied from Excel (Ctrl+V), or fill our Excel format and upload it. Press Save when done; nothing is saved before that.
	</p>
</Card>

{#if problems.length}
	<p class="mb-3 flex items-center gap-2 rounded-md border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-800 dark:bg-red-950/40 dark:text-red-200">
		<CircleAlert class="size-4 shrink-0" />Nothing was saved. Please fix {problems.length === 1 ? 'this' : 'these'} first:
	</p>
	<ul class="-mt-3 mb-3 list-disc rounded-b-md border border-t-0 border-red-300 bg-red-50 py-2 pl-9 pr-3 text-sm text-red-800 dark:bg-red-950/40 dark:text-red-200" data-testid="bulk-problems">
		{#each problems.slice(0, 10) as p, i (i)}<li>{p}</li>{/each}
		{#if problems.length > 10}<li>…and {problems.length - 10} more, marked in red below.</li>{/if}
	</ul>
{/if}

<Card bodyClass="p-0">
	{#if loading}
		<p class="p-6 text-center text-sm text-muted-foreground">Loading…</p>
	{:else if loadError}
		<p class="p-6 text-center text-sm text-destructive">{loadError}</p>
	{:else}
		<div class="max-h-[70vh] overflow-auto">
			<table class="table-base bulk-grid text-sm" data-testid="bulk-grid">
				<thead class="sticky top-0 z-10 bg-card">
					<tr>
						<th class="w-10">#</th>
						{#each ALL_COLUMNS as c (c.key)}<th class={cn(c.width, c.kind === 'number' && 'num')}>{c.label}{#if c.required}<span class="text-destructive">*</span>{/if}</th>{/each}
						<th class="w-8"></th>
					</tr>
				</thead>
				<tbody>
					{#each visible as r, ri (r.key)}
						{@const changed = changedKeys(r)}
						{@const rowErr = errors[r.key] ?? {}}
						<tr class={cn(r.id === null && !isBlank(r) && 'bg-emerald-50 dark:bg-emerald-950/30', r.id !== null && changed.length && 'bg-amber-50 dark:bg-amber-950/30', rowErr.row && 'bg-red-50')}>
							<td class="text-xs text-muted-foreground" title={rowErr.row ?? ''}>{r.id === null ? 'new' : ri + 1}</td>
							{#each ALL_COLUMNS as c, ci (c.key)}
								{@const err = rowErr[c.key]}
								<td class="p-0.5">
									{#if c.kind === 'bool'}
										<input type="checkbox" class="mx-auto block size-4" bind:checked={r.values[c.key] as boolean} data-cell="{ri}-{ci}"
											onpaste={(e) => onPaste(e, ri, ci)} aria-label="{c.label} row {ri + 1}" />
									{:else if c.kind === 'select'}
										<select bind:value={r.values[c.key]} data-cell="{ri}-{ci}" title={err ? plain(err) : ''} onpaste={(e) => onPaste(e, ri, ci)}
											class={cn('h-8 w-full rounded border border-transparent bg-transparent px-1.5 hover:border-input focus:border-ring focus:outline-none', changed.includes(c.key) && 'font-semibold', err && 'border-red-500')}
											aria-label="{c.label} row {ri + 1}">
											{#each c.options ?? [] as o (o.value)}<option value={o.value}>{o.label}</option>{/each}
										</select>
									{:else}
										<input
											type="text"
											inputmode={c.kind === 'number' ? 'decimal' : undefined}
											bind:value={r.values[c.key]}
											data-cell="{ri}-{ci}"
											title={err ? plain(err) : ''}
											onpaste={(e) => onPaste(e, ri, ci)}
											onkeydown={(e) => onKey(e, ri, ci)}
											class={cn(
												'h-8 w-full rounded border border-transparent bg-transparent px-1.5 hover:border-input focus:border-ring focus:bg-background focus:outline-none',
												c.kind === 'number' && 'text-right tabular-nums',
												changed.includes(c.key) && 'font-semibold',
												err && 'border-red-500 bg-red-50 dark:bg-red-950/40'
											)}
											aria-label="{c.label} row {ri + 1}"
										/>
									{/if}
								</td>
							{/each}
							<td class="p-0.5">
								{#if r.id === null}
									<button class="rounded p-1 text-muted-foreground hover:text-destructive" onclick={() => removeRow(r)} aria-label="Remove row"><Trash2 class="size-4" /></button>
								{/if}
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
		<p class="border-t px-3 py-2 text-xs text-muted-foreground">
			<span class="mr-1.5 inline-block size-3 rounded-sm bg-amber-100 align-middle"></span>edited, not saved yet
			<span class="ml-4 mr-1.5 inline-block size-3 rounded-sm bg-emerald-100 align-middle"></span>new, not saved yet
		</p>
	{/if}
</Card>
