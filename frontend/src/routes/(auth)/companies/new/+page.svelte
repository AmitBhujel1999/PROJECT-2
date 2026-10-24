<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Select from '$lib/components/ui/select.svelte';
	import Textarea from '$lib/components/ui/textarea.svelte';

	/** First run of the desktop program: sets up the main company itself, no approval needed. */
	const setup = page.url.searchParams.get('setup') === '1';
	let companies = $state<{ slug: string; name: string }[]>([]);
	let form = $state({
		name: '',
		address: '',
		pan_vat_no: '',
		phone: '',
		email: '',
		admin_name: '',
		admin_username: '',
		admin_password: '',
		authorize_company: page.url.searchParams.get('from') ?? 'main',
		authorize_username: '',
		authorize_password: '',
		date_system: 'BS',
		demo: false
	});
	let errors = $state<Record<string, string>>({});
	let error = $state('');
	let busy = $state(false);

	api.get<{ companies: { slug: string; name: string }[] }>('companies/').then((res) => (companies = res.companies));

	async function submit(e: SubmitEvent) {
		e.preventDefault();
		errors = {};
		error = '';
		busy = true;
		try {
			const res = await api.post<{ slug: string }>('companies/', { ...form, name: form.name.trim(), setup });
			if (setup) {
				await auth.login(form.admin_username.trim(), form.admin_password, res.data.slug);
				await goto('/dashboard', { replaceState: true });
				return;
			}
			if (auth.user) await auth.logout();
			await goto(`/login?company=${encodeURIComponent(res.data.slug)}`);
		} catch (err) {
			errors = fieldErrors(err);
			// Show the message too when no field on this form carries the error.
			if (!Object.keys(errors).some((k) => k in form)) error = err instanceof ApiError ? err.message : 'Could not create the company.';
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>{setup ? 'Set up your company' : 'New company'} · Mero Lekhapal</title></svelte:head>

<div class="grid overflow-hidden rounded-2xl border bg-background shadow-2xl lg:grid-cols-[minmax(0,5fr)_minmax(0,9fr)]">
	<!-- Welcome panel -->
	<aside class="relative flex flex-col justify-between gap-8 overflow-hidden bg-gradient-to-br from-blue-900 via-blue-950 to-slate-900 p-8 text-white">
		<svg class="pointer-events-none absolute -right-10 bottom-0 w-[130%] opacity-[0.08]" viewBox="0 0 64 30" aria-hidden="true"
			><path d="M0 30 L14 12 L22 21 L34 2 L44 14 L52 8 L64 22 L64 30 Z" fill="#fff" /></svg
		>
		<div class="relative">
			<img src="/favicon.svg" alt="" class="mb-5 size-14 rounded-xl shadow-lg shadow-black/30" />
			<h1 class="text-2xl font-semibold tracking-tight">Mero Lekhapal</h1>
			<p class="text-xs font-medium tracking-widest text-white/50 uppercase">Accounting &amp; Inventory</p>
			<p class="mt-6 text-lg leading-snug text-white/90">
				{setup ? 'Welcome! Let’s set up your company.' : 'Add another company.'}
			</p>
			<p class="mt-2 text-sm text-white/60">
				{setup
					? 'This takes about a minute. You can change every detail later under Settings.'
					: 'It starts empty: no products, parties, transactions or demo users. Existing companies stay as they are.'}
			</p>
		</div>
		<ul class="relative grid gap-3 text-sm text-white/80">
			<li class="flex gap-3"><span class="grid size-6 shrink-0 place-items-center rounded-full bg-white/15 text-xs font-semibold">1</span>Business details for invoices and reports</li>
			<li class="flex gap-3"><span class="grid size-6 shrink-0 place-items-center rounded-full bg-white/15 text-xs font-semibold">2</span>Your admin login (also used on the phone app)</li>
			<li class="flex gap-3"><span class="grid size-6 shrink-0 place-items-center rounded-full bg-white/15 text-xs font-semibold">3</span>Start: sales, purchases, stock and VAT reports</li>
		</ul>
	</aside>

	<!-- Form -->
	<form class="grid content-start gap-6 p-8" onsubmit={submit} novalidate>
		<section class="grid gap-4">
			<div>
				<h2 class="text-base font-semibold">Business details</h2>
				<p class="text-sm text-muted-foreground">Shown on invoices, statements and reports.</p>
			</div>
			<div class="grid items-start gap-4 sm:grid-cols-2">
				<Field label="Company name" for="c-name" required error={errors.name} class="sm:col-span-2">
					<Input id="c-name" bind:value={form.name} required autofocus />
				</Field>
				<Field label="PAN/VAT No." for="c-pan" error={errors.pan_vat_no}>
					<Input id="c-pan" bind:value={form.pan_vat_no} />
				</Field>
				<Field label="Phone" for="c-phone" error={errors.phone}>
					<Input id="c-phone" bind:value={form.phone} />
				</Field>
				<Field label="Email" for="c-email" error={errors.email}>
					<Input id="c-email" type="email" bind:value={form.email} />
				</Field>
				{#if setup}
					<Field label="Dates" for="c-dates" hint="You can change this later.">
						<Select id="c-dates" bind:value={form.date_system} options={[{ value: 'BS', label: 'Nepali (B.S.)' }, { value: 'AD', label: 'English (A.D.)' }]} />
					</Field>
				{/if}
				<Field label="Address" for="c-address" error={errors.address} class="sm:col-span-2">
					<Textarea id="c-address" rows={2} bind:value={form.address} />
				</Field>
			</div>
		</section>

		<section class="grid gap-4 border-t pt-6">
			<div>
				<h2 class="text-base font-semibold">{setup ? 'Your admin login' : 'Admin login for the new company'}</h2>
				<p class="text-sm text-muted-foreground">You will sign in with this. Use at least 8 characters for the password.</p>
			</div>
			<div class="grid items-start gap-4 sm:grid-cols-3">
				<Field label="Your name" for="a-name" error={errors.admin_name}>
					<Input id="a-name" bind:value={form.admin_name} />
				</Field>
				<Field label="Username" for="a-user" required error={errors.admin_username}>
					<Input id="a-user" autocomplete="off" bind:value={form.admin_username} required />
				</Field>
				<Field label="Password" for="a-pw" required error={errors.admin_password}>
					<Input id="a-pw" type="password" autocomplete="new-password" bind:value={form.admin_password} required />
				</Field>
			</div>
		</section>

		{#if !setup}
			<section class="grid gap-4 border-t pt-6">
				<div>
					<h2 class="text-base font-semibold">Approve with an existing admin</h2>
					<p class="text-sm text-muted-foreground">An administrator of one of your companies must allow this.</p>
				</div>
				<div class="grid items-start gap-4 sm:grid-cols-3">
					<Field label="Company" for="z-company" error={errors.authorize_company}>
						<Select id="z-company" bind:value={form.authorize_company} options={companies.map((c) => ({ value: c.slug, label: c.name }))} />
					</Field>
					<Field label="Admin username" for="z-user" required error={errors.authorize_username}>
						<Input id="z-user" autocomplete="username" bind:value={form.authorize_username} required />
					</Field>
					<Field label="Admin password" for="z-pw" required error={errors.authorize_password}>
						<Input id="z-pw" type="password" autocomplete="current-password" bind:value={form.authorize_password} required />
					</Field>
				</div>
			</section>
		{/if}

		{#if error}<p class="rounded-md bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">{error}</p>{/if}

		<div class="flex flex-col-reverse items-stretch gap-3 border-t pt-6 sm:flex-row sm:items-center sm:justify-between">
			{#if setup}
				<label class="flex items-start gap-2 text-sm">
					<input type="checkbox" bind:checked={form.demo} class="mt-0.5" />
					<span>Add demo data to try the program<span class="block text-xs text-muted-foreground">Every demo record is marked "(Demo)".</span></span>
				</label>
			{:else}
				<a href="/login" class="text-sm text-muted-foreground hover:text-foreground">Back to login</a>
			{/if}
			<Button type="submit" size="lg" loading={busy} class="sm:min-w-44">{busy ? (setup ? 'Setting up…' : 'Creating company…') : setup ? 'Start' : 'Create company'}</Button>
		</div>
	</form>
</div>
