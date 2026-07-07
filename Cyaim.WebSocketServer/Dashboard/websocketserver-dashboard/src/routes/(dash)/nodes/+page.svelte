<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { getNodes } from '$lib/api/dashboard';
	import type { NodeStatusInfo } from '$lib/types/dashboard';
	import { m } from '$lib/paraglide/messages';

	let nodes: NodeStatusInfo[] = [];
	let loading = true;
	let error: string | null = null;
	let refreshInterval: ReturnType<typeof setInterval> | null = null;

	const fetchNodes = async () => {
		try {
			loading = true;
			const response = await getNodes();
			if (response.success && response.data) {
				nodes = response.data;
				error = null;
			} else {
				error = response.error || 'Failed to fetch nodes';
			}
		} catch (err) {
			error = err instanceof Error ? err.message : 'Unknown error';
		} finally {
			loading = false;
		}
	};

	onMount(() => {
		fetchNodes();
		refreshInterval = setInterval(fetchNodes, 2000);
	});

	onDestroy(() => {
		if (refreshInterval) {
			clearInterval(refreshInterval);
		}
	});
</script>

<div class="space-y-6">
	<h2 class="text-2xl font-bold text-gray-800">{m.dashboard_nav_nodes()}</h2>

	{#if loading && nodes.length === 0}
		<div class="text-center py-12">
			<p class="text-gray-600">{m.dashboard_common_loading()}</p>
		</div>
	{:else if error}
		<div class="bg-red-50 border border-red-200 rounded-lg p-4">
			<p class="text-red-800">{m.dashboard_common_error()}: {error}</p>
		</div>
	{:else if nodes.length === 0}
		<div class="bg-white rounded-lg shadow p-12 text-center">
			<p class="text-gray-600 text-lg">No nodes found</p>
		</div>
	{:else}
		<div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
			{#each nodes as node}
				<div
					class="bg-white rounded-lg shadow p-6 border-l-4 {node.isLeader
						? 'border-green-500'
						: 'border-gray-300'}"
				>
					<div class="flex justify-between items-center mb-4">
						<h3 class="text-lg font-semibold text-gray-800">{node.nodeId}</h3>
						{#if node.isLeader}
							<span class="bg-green-500 text-white px-3 py-1 rounded-full text-xs font-semibold">
								{m.dashboard_overview_leader()}
							</span>
						{/if}
					</div>
					<div class="space-y-2 text-sm">
						<div class="flex justify-between">
							<span class="text-gray-600">{m.dashboard_overview_state()}:</span>
							<span class="font-semibold">{node.state}</span>
						</div>
						<div class="flex justify-between">
							<span class="text-gray-600">{m.dashboard_overview_connections()}:</span>
							<span class="font-semibold">{node.connectionCount}</span>
						</div>
						{#if node.currentTerm !== undefined}
							<div class="flex justify-between">
								<span class="text-gray-600">{m.dashboard_overview_term()}:</span>
								<span class="font-semibold">{node.currentTerm}</span>
							</div>
						{/if}
						<div class="flex justify-between">
							<span class="text-gray-600">{m.dashboard_overview_connected()}:</span>
							<span class="font-semibold">
								{node.isConnected ? m.dashboard_overview_yes() : m.dashboard_overview_no()}
							</span>
						</div>
						{#if node.address}
							<div class="flex justify-between">
								<span class="text-gray-600">Address:</span>
								<span class="font-semibold">{node.address}:{node.port || 'N/A'}</span>
							</div>
						{/if}
					</div>
				</div>
			{/each}
		</div>
	{/if}
</div>

