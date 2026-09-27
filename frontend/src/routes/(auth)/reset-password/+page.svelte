<script lang="ts">
	import { page } from '$app/state';
	import { api, ApiError } from '$lib/api/client';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Field from '$lib/components/ui/field.svelte';

	let password = $state('');
	let confirm = $state('');
	let error = $state('');
	let done = $state('');
	let busy = $state(false);

	async function submit(e: SubmitEvent) {
		e.preventDefault();
		error = '';
		if (password !== confirm) {
			error = 'Passwords do not match.';
			return;
		}
		busy = true;
		try {
			const res = await api.post('auth/password/reset/confirm/', {
				uid: page.url.searchParams.get('uid'),
				token: page.url.searchParams.get('token'),
				new_password: password
			});
			done = res.message;
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Reset failed.';
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Choose a new password · Accounting</title></svelte:head>

<h2 class="mb-4 text-base font-semibold">Choose a new password</h2>
{#if done}
	<p class="rounded-md bg-emerald-50 px-3 py-2 text-sm text-emerald-800">{done}</p>
	<a href="/login" class="mt-4 block text-center text-sm font-medium text-primary">Go to login</a>
{:else}
	<form class="grid gap-4" onsubmit={submit}>
		<Field label="New password" for="pw" required hint="At least 8 characters, not entirely numeric.">
			<Input id="pw" type="password" autocomplete="new-password" bind:value={password} required />
		</Field>
		<Field label="Confirm password" for="pw2" required>
			<Input id="pw2" type="password" autocomplete="new-password" bind:value={confirm} required />
		</Field>
		{#if error}<p class="text-sm text-destructive" role="alert">{error}</p>{/if}
		<Button type="submit" loading={busy}>Reset password</Button>
	</form>
{/if}
