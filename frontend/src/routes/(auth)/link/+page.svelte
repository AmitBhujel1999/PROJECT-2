<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { onMount } from 'svelte';
	import { ApiError } from '$lib/api/client';
	import { cloudLink, cloudMode, loadBook, pair, saveLink, type Book } from '$lib/cloud/cloud';
	import { auth } from '$lib/stores/auth.svelte';
	import Button from '$lib/components/ui/button.svelte';

	/**
	 * Online copy: link this phone (the link from "Use this phone anywhere" on the shop Wi-Fi
	 * carries ?b=book&c=one-time-code), or show which company it is linked to.
	 */
	let stage = $state<'checking' | 'linking' | 'linked' | 'unlinked'>('checking');
	let book = $state<Book | null>(null);
	let error = $state('');

	function deviceName(): string {
		const ua = navigator.userAgent;
		if (/iPhone/.test(ua)) return 'iPhone';
		if (/iPad/.test(ua)) return 'iPad';
		const android = ua.match(/Android[^;]*;\s*([^;)]+)/);
		if (android) return android[1].replace(/Build.*/, '').trim();
		return 'Phone';
	}

	onMount(async () => {
		if (!cloudMode) {
			goto('/dashboard', { replaceState: true });
			return;
		}
		const b = page.url.searchParams.get('b');
		const c = page.url.searchParams.get('c');
		if (b && c) {
			stage = 'linking';
			try {
				await pair(b, c, deviceName());
				history.replaceState(null, '', '/link');
				await open();
				return;
			} catch (err) {
				error = err instanceof ApiError ? err.message : 'Linking failed. Try again from the computer.';
			}
		}
		try {
			book = cloudLink() ? await loadBook(true) : null;
		} catch (err) {
			error = err instanceof ApiError ? err.message : 'Could not reach the online copy.';
		}
		stage = book ? 'linked' : 'unlinked';
	});

	async function open() {
		await auth.load();
		goto('/dashboard', { replaceState: true });
	}

	function unlink() {
		if (!confirm('Unlink this phone? You will need the computer to link it again.')) return;
		saveLink(null);
		auth.user = null;
		book = null;
		stage = 'unlinked';
	}

	function ago(iso: string): string {
		const mins = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
		if (mins < 1) return 'just now';
		if (mins < 60) return `${mins} min ago`;
		const hours = Math.round(mins / 60);
		return hours < 48 ? `${hours} h ago` : new Date(iso).toLocaleString();
	}
</script>

<svelte:head><title>Phone anywhere · Mero Lekhapal</title></svelte:head>

{#if stage === 'checking' || stage === 'linking'}
	<div class="grid place-items-center gap-3 py-6 text-center">
		<div class="size-8 animate-spin rounded-full border-4 border-primary/20 border-t-primary"></div>
		<p class="text-sm text-muted-foreground">{stage === 'linking' ? 'Linking this phone… keep Mero Lekhapal open on the computer.' : 'Opening…'}</p>
	</div>
{:else if stage === 'linked' && book}
	<h2 class="mb-1 text-base font-semibold">{book.name}</h2>
	<p class="mb-4 text-sm text-muted-foreground">
		Online copy · signed in as <b>{book.me?.display_name ?? book.me?.username}</b><br />
		Last uploaded from the computer: {book.updated ? ago(book.updated) : 'not yet'}
	</p>
	<div class="grid gap-2">
		<Button class="w-full" onclick={open}>Open</Button>
		<Button variant="outline" class="w-full" onclick={unlink}>Unlink this phone</Button>
	</div>
{:else}
	<h2 class="mb-1 text-base font-semibold">See your books anywhere</h2>
	{#if error}<p class="mb-3 rounded-md bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">{error}</p>{/if}
	<ol class="grid list-decimal gap-2 pl-5 text-sm text-muted-foreground">
		<li>Connect this phone to the same Wi-Fi as the computer running Mero Lekhapal.</li>
		<li>Open Mero Lekhapal on the phone (the computer's address) and sign in.</li>
		<li>Tap <b>More → Use this phone anywhere</b>.</li>
	</ol>
	<p class="mt-3 text-xs text-muted-foreground">An administrator must first switch on <b>Settings → Phone Anywhere</b> on the computer.</p>
{/if}
