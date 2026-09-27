<script lang="ts">
	import { page } from '$app/state';
</script>

<svelte:head><title>{page.status} · Accounting</title></svelte:head>
<div class="grid min-h-screen place-items-center p-6 text-center">
	<div>
		<h1 class="text-5xl font-bold text-primary">{page.status}</h1>
		<p class="mt-2 text-lg font-medium">{page.status === 404 ? 'Page not found' : 'Something went wrong'}</p>
		<p class="mt-1 text-sm text-muted-foreground">{page.error?.message}</p>
		<a href="/dashboard" class="mt-6 inline-block rounded-md bg-primary px-4 py-2 text-sm text-white">Go to dashboard</a>
	</div>
</div>
