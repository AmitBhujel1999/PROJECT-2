<script lang="ts">
	import { Popover } from 'bits-ui';
	import { CalendarDays, ChevronLeft, ChevronRight } from '@lucide/svelte';
	import type { HTMLInputAttributes } from 'svelte/elements';
	import { cn } from '$lib/utils';
	import { today } from '$lib/utilities/format';
	import {
		adToBs,
		bsToAd,
		daysInBsMonth,
		parseBs,
		BS_MAX_YEAR,
		BS_MIN_YEAR,
		BS_MONTHS,
		type BsDate
	} from '$lib/utilities/nepali-date';

	/**
	 * Nepali (B.S.) date field. `value` stays an A.D. ISO date ("2026-10-13"),
	 * exactly like <input type="date">, so callers and the API are unchanged.
	 * Type "2083/06/27" or pick from the calendar.
	 */
	let {
		value = $bindable(),
		class: className,
		ref = $bindable(null),
		min,
		max,
		disabled,
		onchange,
		type: _type,
		...rest
	}: HTMLInputAttributes & { ref?: HTMLInputElement | null } = $props();

	const pad = (n: number) => String(n).padStart(2, '0');
	const show = (bs: BsDate) => `${bs.year}/${pad(bs.month)}/${pad(bs.day)}`;

	function toBs(iso: unknown): BsDate | null {
		if (typeof iso !== 'string' || !iso) return null;
		try {
			return adToBs(iso);
		} catch {
			return null;
		}
	}

	const current = $derived(toBs(value));
	let text = $state('');
	let invalid = $state(false);
	let open = $state(false);
	// Month shown in the calendar grid.
	let viewYear = $state(2083);
	let viewMonth = $state(1);

	$effect(() => {
		text = current ? show(current) : '';
		invalid = false;
	});

	$effect(() => {
		if (open) {
			const bs = current ?? toBs(today())!;
			viewYear = bs.year;
			viewMonth = bs.month;
		}
	});

	function outOfRange(iso: string): boolean {
		return (typeof min === 'string' && min !== '' && iso < min) || (typeof max === 'string' && max !== '' && iso > max);
	}

	function commit(iso: string, event: Event) {
		if (iso === value) return;
		value = iso;
		onchange?.(event as Event & { currentTarget: EventTarget & HTMLInputElement });
	}

	function onTextChange(event: Event) {
		if (text.trim() === '') {
			invalid = false;
			commit('', event);
			return;
		}
		const bs = parseBs(text);
		if (!bs || outOfRange(bsToAd(bs))) {
			invalid = true;
			return;
		}
		invalid = false;
		text = show(bs);
		commit(bsToAd(bs), event);
	}

	function pick(day: number, event: Event) {
		commit(bsToAd({ year: viewYear, month: viewMonth, day }), event);
		open = false;
		ref?.focus();
	}

	function step(delta: number) {
		let m = viewMonth + delta;
		let y = viewYear;
		if (m < 1) [m, y] = [12, y - 1];
		if (m > 12) [m, y] = [1, y + 1];
		if (y < BS_MIN_YEAR || y > BS_MAX_YEAR) return;
		viewYear = y;
		viewMonth = m;
	}

	const years = Array.from({ length: BS_MAX_YEAR - BS_MIN_YEAR + 1 }, (_, i) => BS_MIN_YEAR + i);
	const todayBs = $derived(toBs(today()));
	const grid = $derived.by(() => {
		const first = bsToAd({ year: viewYear, month: viewMonth, day: 1 });
		const lead = new Date(`${first}T00:00:00Z`).getUTCDay();
		const days = daysInBsMonth(viewYear, viewMonth);
		const cells: ({ day: number; iso: string } | null)[] = Array(lead).fill(null);
		for (let d = 1; d <= days; d++) cells.push({ day: d, iso: bsToAd({ year: viewYear, month: viewMonth, day: d }) });
		return cells;
	});
	const adHint = $derived.by(() => {
		if (typeof value !== 'string' || !value) return '';
		const [y, m, d] = value.split('-').map(Number);
		return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
	});
</script>

<div class={cn('relative flex h-9 w-full min-w-0', className)}>
	<input
		bind:this={ref}
		bind:value={text}
		type="text"
		inputmode="numeric"
		placeholder="YYYY/MM/DD"
		autocomplete="off"
		title={adHint ? `${adHint} A.D.` : 'Nepali date (B.S.)'}
		aria-invalid={invalid || undefined}
		{disabled}
		onchange={onTextChange}
		onkeydown={(e) => {
			if (e.key === 'ArrowDown' && e.altKey) {
				e.preventDefault();
				open = true;
			}
		}}
		class="flex h-full min-h-8 w-full min-w-0 rounded-md border border-input bg-background py-1 pl-3 pr-9 text-sm tabular-nums shadow-xs transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/60 disabled:cursor-not-allowed disabled:opacity-60 aria-[invalid=true]:border-destructive aria-[invalid=true]:ring-destructive/30"
		{...rest}
	/>
	<Popover.Root bind:open>
		<Popover.Trigger
			{disabled}
			class="absolute inset-y-0 right-0 flex w-8 items-center justify-center rounded-r-md text-muted-foreground hover:text-foreground disabled:opacity-60"
			aria-label="Open Nepali calendar"
			tabindex={-1}
		>
			<CalendarDays class="size-4" />
		</Popover.Trigger>
		<Popover.Portal>
			<Popover.Content
				align="end"
				sideOffset={4}
				class="z-50 w-[17.5rem] rounded-lg border bg-background p-3 text-sm shadow-lg focus:outline-none"
				onOpenAutoFocus={(e) => e.preventDefault()}
			>
				<div class="mb-2 flex items-center gap-1">
					<button type="button" class="rounded p-1 hover:bg-muted" aria-label="Previous month" onclick={() => step(-1)}><ChevronLeft class="size-4" /></button>
					<select bind:value={viewMonth} class="h-7 flex-1 rounded border border-input bg-background px-1 text-sm" aria-label="Month">
						{#each BS_MONTHS as name, i (name)}<option value={i + 1}>{name}</option>{/each}
					</select>
					<select bind:value={viewYear} class="h-7 w-[4.5rem] rounded border border-input bg-background px-1 text-sm" aria-label="Year">
						{#each years as y (y)}<option value={y}>{y}</option>{/each}
					</select>
					<button type="button" class="rounded p-1 hover:bg-muted" aria-label="Next month" onclick={() => step(1)}><ChevronRight class="size-4" /></button>
				</div>
				<div class="grid grid-cols-7 gap-0.5 text-center">
					{#each ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa'] as wd (wd)}
						<div class={cn('py-1 text-xs font-medium text-muted-foreground', wd === 'Sa' && 'text-destructive')}>{wd}</div>
					{/each}
					{#each grid as cell, i (i)}
						{#if cell}
							{@const selected = cell.iso === value}
							{@const isToday = todayBs && todayBs.year === viewYear && todayBs.month === viewMonth && todayBs.day === cell.day}
							<button
								type="button"
								disabled={outOfRange(cell.iso)}
								onclick={(e) => pick(cell.day, e)}
								class={cn(
									'h-8 rounded-md tabular-nums hover:bg-muted disabled:pointer-events-none disabled:opacity-35',
									i % 7 === 6 && 'text-destructive',
									isToday && 'ring-1 ring-primary',
									selected && 'bg-primary text-primary-foreground hover:bg-primary'
								)}
							>
								{cell.day}
							</button>
						{:else}
							<div></div>
						{/if}
					{/each}
				</div>
				<div class="mt-2 flex items-center justify-between border-t pt-2 text-xs text-muted-foreground">
					<span>{adHint ? `${adHint} A.D.` : ''}</span>
					<button
						type="button"
						class="rounded px-2 py-1 font-medium text-primary hover:bg-muted disabled:opacity-40"
						disabled={outOfRange(today())}
						onclick={(e) => {
							commit(today(), e);
							open = false;
						}}
					>
						Today
					</button>
				</div>
			</Popover.Content>
		</Popover.Portal>
	</Popover.Root>
</div>
