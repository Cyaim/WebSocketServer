<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { getClients, sendMessage } from '$lib/api/dashboard';
	import type { ClientConnectionInfo, SendMessageRequest } from '$lib/types/dashboard';
	import { m } from '$lib/paraglide/messages';

	let connectionId = '';
	let message = '';
	let messageType: 'Text' | 'Binary' = 'Text';
	let loading = false;
	let result: { success: boolean; error?: string } | null = null;
	let clients: ClientConnectionInfo[] = [];
	let refreshInterval: ReturnType<typeof setInterval> | null = null;

	const fetchClients = async () => {
		try {
			const response = await getClients();
			if (response.success && response.data) {
				clients = response.data;
			}
		} catch (err) {
			console.error('Error fetching clients:', err);
		}
	};

	const handleSend = async () => {
		if (!connectionId || !message) {
			result = {
				success: false,
				error: !connectionId
					? m.dashboard_send_connectionIdRequired()
					: m.dashboard_send_contentRequired()
			};
			return;
		}

		try {
			loading = true;
			result = null;

			const request: SendMessageRequest = {
				connectionId,
				content: message,
				messageType
			};

			const response = await sendMessage(request);
			result = {
				success: response.success || false,
				error: response.error
			};

			if (response.success) {
				message = '';
			}
		} catch (err) {
			result = {
				success: false,
				error: err instanceof Error ? err.message : 'Unknown error'
			};
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
</script>

<div class="space-y-6">
	<h2 class="text-2xl font-bold text-gray-800">{m.dashboard_send_title()}</h2>

	<!-- Send Form -->
	<div class="bg-white rounded-lg shadow p-6 space-y-4">
		<div>
			<label for="connectionId" class="block text-sm font-medium text-gray-700 mb-2">
				{m.dashboard_send_connectionId()}
			</label>
			<input
				id="connectionId"
				type="text"
				bind:value={connectionId}
				placeholder={m.dashboard_send_connectionIdPlaceholder()}
				list="client-list"
				class="w-full px-4 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-purple-500 focus:border-transparent"
			/>
			<datalist id="client-list">
				{#each clients as client}
					<option value={client.connectionId} />
				{/each}
			</datalist>
		</div>

		<div>
			<label for="messageType" class="block text-sm font-medium text-gray-700 mb-2">
				{m.dashboard_send_messageType()}
			</label>
			<select
				id="messageType"
				bind:value={messageType}
				class="w-full px-4 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-purple-500 focus:border-transparent"
			>
				<option value="Text">{m.dashboard_dataflow_text()}</option>
				<option value="Binary">{m.dashboard_dataflow_binary()}</option>
			</select>
		</div>

		<div>
			<label for="message" class="block text-sm font-medium text-gray-700 mb-2">
				{m.dashboard_send_content()}
			</label>
			<textarea
				id="message"
				bind:value={message}
				placeholder={m.dashboard_send_contentPlaceholder()}
				rows="6"
				class="w-full px-4 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-purple-500 focus:border-transparent"
			></textarea>
		</div>

		<button
			onclick={handleSend}
			disabled={loading || !connectionId || !message}
			class="w-full px-6 py-3 bg-purple-600 text-white rounded-lg font-semibold hover:bg-purple-700 disabled:bg-gray-400 disabled:cursor-not-allowed transition-colors"
		>
			{loading ? m.dashboard_send_sending() : m.dashboard_send_send()}
		</button>

		{#if result}
			<div
				class="p-4 rounded-lg {result.success
					? 'bg-green-50 border border-green-200 text-green-800'
					: 'bg-red-50 border border-red-200 text-red-800'}"
			>
				{result.success ? '✓ ' + m.dashboard_send_success() : '✗ ' + m.dashboard_send_error() + ': ' + (result.error || 'Unknown error')}
			</div>
		{/if}
	</div>

	<!-- Available Connections -->
	<div class="bg-white rounded-lg shadow p-6">
		<h3 class="text-lg font-semibold text-gray-800 mb-4">
			{m.dashboard_send_availableConnections()} ({clients.length})
		</h3>
		{#if clients.length === 0}
			<div class="text-center py-8 text-gray-600">{m.dashboard_send_noConnections()}</div>
		{:else}
			<div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
				{#each clients as client}
					<div
						onclick={() => (connectionId = client.connectionId)}
						class="p-4 border-2 rounded-lg cursor-pointer transition-all {connectionId === client.connectionId
							? 'border-purple-500 bg-purple-50'
							: 'border-gray-200 hover:border-purple-300'}"
					>
						<div class="font-semibold text-gray-800 mb-2">
							<code class="text-sm">{client.connectionId}</code>
						</div>
						<div class="flex gap-2 items-center text-sm">
							<span
								class="px-2 py-1 text-xs rounded-full {client.state === 'Open'
									? 'bg-green-100 text-green-800'
									: 'bg-orange-100 text-orange-800'}"
							>
								{client.state}
							</span>
							<span class="text-gray-600">{client.nodeId}</span>
						</div>
					</div>
				{/each}
			</div>
		{/if}
	</div>
</div>

