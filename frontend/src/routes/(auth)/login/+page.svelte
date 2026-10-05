<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { auth } from '$lib/stores/auth.svelte';
	import { ApiError } from '$lib/api/client';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Field from '$lib/components/ui/field.svelte';

	let username = $state('');
	let password = $state('');
	let error = $state('');
	let busy = $state(false);

	function safeNext(): string {
		const next = page.url.searchParams.get('next') ?? '/';
		// Only allow same-site relative paths (no open redirects).
		return next.startsWith('/') && !next.startsWith('//') ? next : '/';
	}

	$effect(() => {
		if (auth.user) goto(safeNext());
	});

	async function submit(e: SubmitEvent) {
		e.preventDefault();
		error = '';
		busy = true;
		try {
			await auth.login(username.trim(), password);
			await goto(safeNext());
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Login failed.';
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Login · Accounting</title></svelte:head>

<form class="grid gap-4" onsubmit={submit} novalidate>
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
