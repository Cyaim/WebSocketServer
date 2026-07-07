<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { getClusterOverview } from '$lib/api/dashboard';
	import type { ClusterOverview } from '$lib/types/dashboard';
	import { m } from '$lib/paraglide/messages';

	let overview: ClusterOverview | null = null;
	let loading = true;
	let error: string | null = null;
	let refreshInterval: ReturnType<typeof setInterval> | null = null;

	const fetchOverview = async () => {
		try {
			loading = true;
			const response = await getClusterOverview();
			if (response.success && response.data) {
				overview = response.data;
				error = null;
			} else {
				error = response.error || 'Failed to fetch overview';
			}
		} catch (err) {
			error = err instanceof Error ? err.message : 'Unknown error';
		} finally {
			loading = false;
		}
	};

	onMount(() => {
		fetchOverview();
		refreshInterval = setInterval(fetchOverview, 2000);
	});

	onDestroy(() => {
		if (refreshInterval) {
			clearInterval(refreshInterval);
		}
	});

	const formatBytes = (bytes: number): string => {
		if (bytes === 0) return '0 B';
		const k = 1024;
		const sizes = ['B', 'KB', 'MB', 'GB'];
		const i = Math.floor(Math.log(bytes) / Math.log(k));
		return Math.round((bytes / Math.pow(k, i)) * 100) / 100 + ' ' + sizes[i];
	};
</script>

<div class="space-y-6">
	<h2 class="text-2xl font-bold text-gray-800">{m.dashboard_overview_title()}</h2>

	{#if loading && !overview}
		<div class="text-center py-12">
			<p class="text-gray-600">{m.dashboard_common_loading()}</p>
		</div>
	{:else if error}
		<div class="bg-red-50 border border-red-200 rounded-lg p-4">
			<p class="text-red-800">{m.dashboard_common_error()}: {error}</p>
		</div>
	{:else if overview}
		<!-- Statistics Cards -->
		<div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_overview_totalNodes()}</div>
				<div class="text-3xl font-bold text-purple-600">{overview.totalNodes}</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_overview_connectedNodes()}</div>
				<div class="text-3xl font-bold text-green-600">{overview.connectedNodes}</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_overview_totalConnections()}</div>
				<div class="text-3xl font-bold text-blue-600">{overview.totalConnections}</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_overview_localConnections()}</div>
				<div class="text-3xl font-bold text-indigo-600">{overview.localConnections}</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_overview_currentNode()}</div>
				<div class="text-xl font-semibold text-gray-800">{overview.currentNodeId}</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_overview_isLeader()}</div>
				<div class="text-3xl font-bold {overview.isCurrentNodeLeader ? 'text-green-600' : 'text-gray-400'}">
					{overview.isCurrentNodeLeader ? m.dashboard_overview_yes() : m.dashboard_overview_no()}
				</div>
			</div>
		</div>

		<!-- Nodes List -->
		<div class="bg-white rounded-lg shadow">
			<div class="p-6 border-b border-gray-200">
				<h3 class="text-xl font-semibold text-gray-800">{m.dashboard_overview_nodes()}</h3>
			</div>
			<div class="p-6">
				<div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
					{#each overview.nodes as node}
						<div
							class="border rounded-lg p-4 {node.isLeader
								? 'border-green-500 bg-green-50'
								: 'border-gray-200'}"
						>
							<div class="flex justify-between items-center mb-3">
								<span class="font-semibold text-gray-800">{node.nodeId}</span>
								{#if node.isLeader}
									<span class="bg-green-500 text-white px-2 py-1 rounded text-xs font-semibold">
										{m.dashboard_overview_leader()}
									</span>
								{/if}
							</div>
							<div class="grid grid-cols-2 gap-2 text-sm">
								<div>
									<span class="text-gray-600">{m.dashboard_overview_state()}:</span>
									<span class="font-semibold ml-1">{node.state}</span>
								</div>
								<div>
									<span class="text-gray-600">{m.dashboard_overview_connections()}:</span>
									<span class="font-semibold ml-1">{node.connectionCount}</span>
								</div>
								{#if node.currentTerm !== undefined}
									<div>
										<span class="text-gray-600">{m.dashboard_overview_term()}:</span>
										<span class="font-semibold ml-1">{node.currentTerm}</span>
									</div>
								{/if}
								<div>
									<span class="text-gray-600">{m.dashboard_overview_connected()}:</span>
									<span class="font-semibold ml-1">
										{node.isConnected ? m.dashboard_overview_yes() : m.dashboard_overview_no()}
									</span>
								</div>
							</div>
						</div>
					{/each}
				</div>
			</div>
		</div>
	{/if}
</div>

