<script lang="ts">
	import { page } from '$app/state';
	import { api, ApiError, fieldErrors } from '$lib/api/client';
	import { auth } from '$lib/stores/auth.svelte';
	import { toast } from '$lib/stores/toast.svelte';
	import type { User } from '$lib/types';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';
	import Field from '$lib/components/ui/field.svelte';
	import Input from '$lib/components/ui/input.svelte';
	import Button from '$lib/components/ui/button.svelte';
	import Tabs from '$lib/components/ui/tabs.svelte';

	let tab = $state(page.url.searchParams.get('tab') === 'password' ? 'password' : 'profile');
	let profile = $state({ first_name: auth.user?.first_name ?? '', last_name: auth.user?.last_name ?? '', email: auth.user?.email ?? '', phone: auth.user?.phone ?? '' });
	let pw = $state({ current_password: '', new_password: '', confirm: '' });
	let errors = $state<Record<string, string>>({});
	let busy = $state(false);

	async function saveProfile(e: SubmitEvent) {
		e.preventDefault();
		busy = true;
		errors = {};
		try {
			const res = await api.patch<User>('auth/me/', profile);
			auth.user = res.data;
			toast.success(res.message);
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError) toast.error(err.message);
		} finally {
			busy = false;
		}
	}

	async function changePassword(e: SubmitEvent) {
		e.preventDefault();
		errors = {};
		if (pw.new_password !== pw.confirm) {
			errors.confirm = 'Passwords do not match.';
			return;
		}
		busy = true;
		try {
			const res = await api.post('auth/password/change/', { current_password: pw.current_password, new_password: pw.new_password });
			toast.success(res.message);
			pw = { current_password: '', new_password: '', confirm: '' };
		} catch (err) {
			errors = fieldErrors(err);
			if (err instanceof ApiError && !Object.keys(errors).length) toast.error(err.message);
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Profile · Accounting</title></svelte:head>
<PageHeader title="My profile" description="{auth.user?.username} · {auth.user?.role}" />
<Tabs tabs={[{ value: 'profile', label: 'Profile' }, { value: 'password', label: 'Change password' }]} bind:value={tab} />

<Card class="max-w-2xl">
	{#if tab === 'profile'}
		<form class="grid gap-4 sm:grid-cols-2" onsubmit={saveProfile}>
			<Field label="First name" for="pr-first"><Input id="pr-first" bind:value={profile.first_name} /></Field>
			<Field label="Last name" for="pr-last"><Input id="pr-last" bind:value={profile.last_name} /></Field>
			<Field label="Email" for="pr-email" error={errors.email}><Input id="pr-email" type="email" bind:value={profile.email} /></Field>
			<Field label="Phone" for="pr-phone"><Input id="pr-phone" bind:value={profile.phone} /></Field>
			<div class="sm:col-span-2"><Button type="submit" loading={busy}>Save profile</Button></div>
		</form>
	{:else}
		<form class="grid max-w-sm gap-4" onsubmit={changePassword}>
			<Field label="Current password" for="pw-cur" required error={errors.current_password}><Input id="pw-cur" type="password" autocomplete="current-password" bind:value={pw.current_password} required /></Field>
			<Field label="New password" for="pw-new" required error={errors.new_password || errors.non_field_errors}><Input id="pw-new" type="password" autocomplete="new-password" bind:value={pw.new_password} required /></Field>
			<Field label="Confirm new password" for="pw-confirm" required error={errors.confirm}><Input id="pw-confirm" type="password" autocomplete="new-password" bind:value={pw.confirm} required /></Field>
			<Button type="submit" loading={busy}>Change password</Button>
		</form>
	{/if}
</Card>
