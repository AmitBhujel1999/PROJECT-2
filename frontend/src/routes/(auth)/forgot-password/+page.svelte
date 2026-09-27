<script lang="ts">
	import { api, ApiError } from '$lib/api/client';
	import Button from '$lib/components/ui/button.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Field from '$lib/components/ui/field.svelte';

	let email = $state('');
	let message = $state('');
	let error = $state('');
	let busy = $state(false);

	async function submit(e: SubmitEvent) {
		e.preventDefault();
		busy = true;
		error = '';
		try {
			const res = await api.post('auth/password/reset/', { email });
			message = res.message;
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Request failed.';
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Reset password · Accounting</title></svelte:head>

<h2 class="mb-1 text-base font-semibold">Reset your password</h2>
<p class="mb-4 text-sm text-muted-foreground">Enter your account email and we'll send you a reset link.</p>
{#if message}
	<p class="rounded-md bg-emerald-50 px-3 py-2 text-sm text-emerald-800" role="status">{message}</p>
{:else}
	<form class="grid gap-4" onsubmit={submit}>
		<Field label="Email" for="email" required>
			<Input id="email" type="email" autocomplete="email" bind:value={email} required />
		</Field>
		{#if error}<p class="text-sm text-destructive" role="alert">{error}</p>{/if}
		<Button type="submit" loading={busy}>Send reset link</Button>
	</form>
{/if}
<a href="/login" class="mt-4 block text-center text-sm text-muted-foreground hover:text-foreground">Back to login</a>
