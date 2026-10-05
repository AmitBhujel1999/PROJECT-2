<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { auth } from '$lib/stores/auth.svelte';
	import { api, ApiError } from '$lib/api/client';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Select from '$lib/components/ui/select.svelte';

	const NEW_COMPANY = '__new__';
	let companies = $state<{ slug: string; name: string }[]>([]);
	let current = $state('main');
	let company = $state('main');
	let username = $state('');
	let password = $state('');
	let error = $state('');
	let busy = $state(false);

	function safeNext(): string {
		const next = page.url.searchParams.get('next') ?? '/dashboard';
		// Only allow same-site relative paths (no open redirects).
		return next.startsWith('/') && !next.startsWith('//') ? next : '/dashboard';
	}

	$effect(() => {
		if (auth.user) goto(safeNext());
	});

	api.get<{ companies: { slug: string; name: string }[]; current: string }>('companies/').then((res) => {
		companies = res.companies;
		current = company = page.url.searchParams.get('company') ?? res.current;
	});

	$effect(() => {
		if (company === NEW_COMPANY) goto(`/companies/new?from=${encodeURIComponent(current)}`);
	});

	async function submit(e: SubmitEvent) {
		e.preventDefault();
		error = '';
		busy = true;
		try {
			await auth.login(username.trim(), password, company);
			// A different company: reload so no cached data of the old one stays on screen.
			if (company !== current) window.location.assign(safeNext());
			else await goto(safeNext());
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Login failed.';
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Login · Accounting</title></svelte:head>

<form class="grid gap-4" onsubmit={submit} novalidate>
	{#if companies.length}
		<Field label="Company" for="company">
			<Select id="company" bind:value={company} options={[...companies.map((c) => ({ value: c.slug, label: c.name })), { value: NEW_COMPANY, label: '+ New company…' }]} />
		</Field>
	{/if}
	<Field label="Username" for="username" required>
		<Input id="username" name="username" autocomplete="username" bind:value={username} required autofocus />
	</Field>
	<Field label="Password" for="password" required>
		<Input id="password" name="password" type="password" autocomplete="current-password" bind:value={password} required />
	</Field>
	{#if error}<p class="rounded-md bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">{error}</p>{/if}
	<Button type="submit" loading={busy} class="w-full">Sign in</Button>
	<a href="/forgot-password" class="text-center text-sm text-muted-foreground hover:text-foreground">Forgot password?</a>
</form>
