<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { Check } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import PageHeader from '$lib/components/ui/page-header.svelte';
	import Card from '$lib/components/ui/card.svelte';

	interface RoleInfo { role: string; label: string; description: string; permissions: string[] }
	const q = createQuery(() => ({ queryKey: ['roles'], queryFn: () => api.get<RoleInfo[]>('roles/') }));
	const allPerms = $derived([...new Set((q.data ?? []).flatMap((r) => r.permissions))].sort());
</script>

<svelte:head><title>Roles · Accounting</title></svelte:head>
<PageHeader title="Roles & permissions" description="Permissions are enforced by the Django API on every request. Assign roles under Users." />
{#if q.data}
	<div class="mb-4 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
		{#each q.data as r (r.role)}
			<Card title={r.label}><p class="text-sm text-muted-foreground">{r.description}</p></Card>
		{/each}
	</div>
	<Card bodyClass="p-0">
		<div class="overflow-x-auto">
			<table class="table-base">
				<thead><tr><th>Permission</th>{#each q.data as r (r.role)}<th class="text-center">{r.label}</th>{/each}</tr></thead>
				<tbody>
					{#each allPerms as perm (perm)}
						<tr>
							<td class="font-mono text-xs">{perm}</td>
							{#each q.data as r (r.role)}
								<td class="text-center">{#if r.permissions.includes(perm)}<Check class="mx-auto size-4 text-emerald-600" aria-label="allowed" />{:else}<span class="text-muted-foreground" aria-label="denied">—</span>{/if}</td>
							{/each}
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</Card>
{/if}
