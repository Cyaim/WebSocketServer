<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { getClients } from '$lib/api/dashboard';
	import type { ClientConnectionInfo } from '$lib/types/dashboard';
	import { m } from '$lib/paraglide/messages';

	let clients: ClientConnectionInfo[] = [];
	let loading = true;
	let error: string | null = null;
	let refreshInterval: ReturnType<typeof setInterval> | null = null;

	const fetchClients = async () => {
		try {
			loading = true;
			const response = await getClients();
			if (response.success && response.data) {
				clients = response.data;
				error = null;
			} else {
				error = response.error || 'Failed to fetch clients';
			}
		} catch (err) {
			error = err instanceof Error ? err.message : 'Unknown error';
		} finally {
			loading = false;
		}
	};

	onMount(() => {
		fetchClients();
		refreshInterval = setInterval(fetchClients, 2000);
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
	<div class="flex justify-between items-center">
		<h2 class="text-2xl font-bold text-gray-800">{m.dashboard_clients_title()}</h2>
		<div class="text-lg font-semibold text-purple-600">
			{m.dashboard_clients_total()}: {clients.length}
		</div>
	</div>

	{#if loading && clients.length === 0}
		<div class="text-center py-12">
			<p class="text-gray-600">{m.dashboard_clients_loading()}</p>
		</div>
	{:else if error}
		<div class="bg-red-50 border border-red-200 rounded-lg p-4">
			<p class="text-red-800">{m.dashboard_common_error()}: {error}</p>
		</div>
	{:else if clients.length === 0}
		<div class="bg-white rounded-lg shadow p-12 text-center">
			<p class="text-gray-600 text-lg">{m.dashboard_clients_noClients()}</p>
		</div>
	{:else}
		<div class="bg-white rounded-lg shadow overflow-hidden">
			<div class="overflow-x-auto">
				<table class="min-w-full divide-y divide-gray-200">
					<thead class="bg-gray-50">
						<tr>
							<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
								{m.dashboard_clients_connectionId()}
							</th>
							<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
								{m.dashboard_clients_nodeId()}
							</th>
							<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
								{m.dashboard_clients_state()}
							</th>
							<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
								{m.dashboard_clients_bytesSent()}
							</th>
							<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
								{m.dashboard_clients_bytesReceived()}
							</th>
							<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
								{m.dashboard_clients_messagesSent()}
							</th>
							<th class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
								{m.dashboard_clients_messagesReceived()}
							</th>
						</tr>
					</thead>
					<tbody class="bg-white divide-y divide-gray-200">
						{#each clients as client}
							<tr class="hover:bg-gray-50">
								<td class="px-6 py-4 whitespace-nowrap">
									<code class="text-sm text-purple-600">{client.connectionId}</code>
								</td>
								<td class="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
									{client.nodeId}
								</td>
								<td class="px-6 py-4 whitespace-nowrap">
									<span
										class="px-2 py-1 text-xs font-semibold rounded-full {client.state === 'Open'
											? 'bg-green-100 text-green-800'
											: 'bg-orange-100 text-orange-800'}"
									>
										{client.state}
									</span>
								</td>
								<td class="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
									{formatBytes(client.bytesSent)}
								</td>
								<td class="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
									{formatBytes(client.bytesReceived)}
								</td>
								<td class="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
									{client.messagesSent}
								</td>
								<td class="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
									{client.messagesReceived}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</div>
	{/if}
</div>

