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
		authorize_password: ''
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
			const res = await api.post<{ slug: string }>('companies/', { ...form, name: form.name.trim() });
			if (auth.user) await auth.logout();
			await goto(`/login?company=${encodeURIComponent(res.data.slug)}`);
		} catch (err) {
			errors = fieldErrors(err);
			if (!Object.keys(errors).length) error = err instanceof ApiError ? err.message : 'Could not create the company.';
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>New company · Accounting</title></svelte:head>

<h2 class="mb-1 text-base font-semibold">New company</h2>
<p class="mb-4 text-sm text-muted-foreground">Starts empty: no products, parties, transactions or demo users. Existing companies stay as they are.</p>

<form class="grid gap-3" onsubmit={submit} novalidate>
	<Field label="Company name" for="c-name" required error={errors.name}>
		<Input id="c-name" bind:value={form.name} required autofocus />
	</Field>
	<Field label="Address" for="c-address" error={errors.address}>
		<Textarea id="c-address" rows={2} bind:value={form.address} />
	</Field>
	<div class="grid grid-cols-2 gap-3">
		<Field label="PAN/VAT No." for="c-pan" error={errors.pan_vat_no}>
			<Input id="c-pan" bind:value={form.pan_vat_no} />
		</Field>
		<Field label="Phone" for="c-phone" error={errors.phone}>
			<Input id="c-phone" bind:value={form.phone} />
		</Field>
	</div>
	<Field label="Email" for="c-email" error={errors.email}>
		<Input id="c-email" type="email" bind:value={form.email} />
	</Field>

	<h3 class="mt-2 border-t pt-3 text-sm font-semibold">Admin login for the new company</h3>
	<Field label="Your name" for="a-name" error={errors.admin_name}>
		<Input id="a-name" bind:value={form.admin_name} />
	</Field>
	<div class="grid grid-cols-2 gap-3">
		<Field label="Username" for="a-user" required error={errors.admin_username}>
			<Input id="a-user" autocomplete="off" bind:value={form.admin_username} required />
		</Field>
		<Field label="Password" for="a-pw" required error={errors.admin_password}>
			<Input id="a-pw" type="password" autocomplete="new-password" bind:value={form.admin_password} required />
		</Field>
	</div>

	<h3 class="mt-2 border-t pt-3 text-sm font-semibold">Approve with an existing admin</h3>
	<Field label="Company" for="z-company" error={errors.authorize_company}>
		<Select id="z-company" bind:value={form.authorize_company} options={companies.map((c) => ({ value: c.slug, label: c.name }))} />
	</Field>
	<div class="grid grid-cols-2 gap-3">
		<Field label="Admin username" for="z-user" required error={errors.authorize_username}>
			<Input id="z-user" autocomplete="username" bind:value={form.authorize_username} required />
		</Field>
		<Field label="Admin password" for="z-pw" required error={errors.authorize_password}>
			<Input id="z-pw" type="password" autocomplete="current-password" bind:value={form.authorize_password} required />
		</Field>
	</div>

	{#if error}<p class="rounded-md bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">{error}</p>{/if}
	<Button type="submit" loading={busy} class="w-full">{busy ? 'Creating company…' : 'Create company'}</Button>
</form>
<a href="/login" class="mt-4 block text-center text-sm text-muted-foreground hover:text-foreground">Back to login</a>
