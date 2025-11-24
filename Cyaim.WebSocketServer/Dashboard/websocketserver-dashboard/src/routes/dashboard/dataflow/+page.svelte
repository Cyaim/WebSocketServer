<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import type { DataFlowMessage } from '$lib/types/dashboard';
	import { m } from '$lib/paraglide/messages';

	let messages: DataFlowMessage[] = [];
	let maxMessages = 100;
	let autoScroll = true;
	let container: HTMLElement;

	// Simulate data flow messages (in real implementation, this would come from WebSocket or SignalR)
	const addMockMessage = () => {
		const directions: ('Inbound' | 'Outbound')[] = ['Inbound', 'Outbound'];
		const types: ('Text' | 'Binary')[] = ['Text', 'Binary'];
		const direction = directions[Math.floor(Math.random() * directions.length)];
		const type = types[Math.floor(Math.random() * types.length)];
		const size = Math.floor(Math.random() * 1000) + 10;

		messages.unshift({
			messageId: `msg-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`,
			connectionId: `conn-${Math.floor(Math.random() * 10)}`,
			nodeId: 'node-1',
			direction,
			messageType: type,
			size,
			content: type === 'Text' ? `Sample message ${messages.length + 1}` : '[Binary Data]',
			timestamp: new Date().toISOString()
		});

		if (messages.length > maxMessages) {
			messages.pop();
		}

		// Auto-scroll
		if (autoScroll && container) {
			container.scrollTop = 0;
		}
	};

	onMount(() => {
		// Add initial messages
		for (let i = 0; i < 10; i++) {
			addMockMessage();
		}

		// Add new message every 2 seconds
		const interval = setInterval(addMockMessage, 2000);
		return () => clearInterval(interval);
	});
</script>

<div class="space-y-6">
	<div class="flex justify-between items-center">
		<h2 class="text-2xl font-bold text-gray-800">{m.dashboard_dataflow_title()}</h2>
		<div class="flex gap-4 items-center">
			<label class="flex items-center gap-2">
				<input type="checkbox" bind:checked={autoScroll} class="rounded" />
				<span class="text-sm text-gray-700">{m.dashboard_dataflow_autoScroll()}</span>
			</label>
			<button
				onclick={() => (messages = [])}
				class="px-4 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 transition-colors"
			>
				{m.dashboard_dataflow_clear()}
			</button>
		</div>
	</div>

	<div
		bind:this={container}
		class="bg-white rounded-lg shadow overflow-y-auto"
		style="max-height: calc(100vh - 300px);"
	>
		<div class="p-4 space-y-4">
			{#each messages as message}
				<div
					class="p-4 rounded-lg border-l-4 {message.direction === 'Inbound'
						? 'border-green-500 bg-green-50'
						: 'border-purple-500 bg-purple-50'}"
				>
					<div class="flex gap-2 items-center mb-2">
						<span
							class="px-2 py-1 text-xs font-semibold rounded-full {message.direction === 'Inbound'
								? 'bg-green-500 text-white'
								: 'bg-purple-500 text-white'}"
						>
							{message.direction === 'Inbound' ? m.dashboard_dataflow_inbound() : m.dashboard_dataflow_outbound()}
						</span>
						<span class="px-2 py-1 text-xs bg-gray-200 text-gray-700 rounded-full">
							{message.messageType === 'Text' ? m.dashboard_dataflow_text() : m.dashboard_dataflow_binary()}
						</span>
						<span class="ml-auto text-xs text-gray-500">
							{new Date(message.timestamp).toLocaleTimeString()}
						</span>
					</div>
					<div class="text-sm text-gray-600 mb-2">
						<span class="font-semibold">{m.dashboard_dataflow_connection()}:</span>
						<code class="ml-1 text-purple-600">{message.connectionId}</code>
						<span class="ml-4 font-semibold">{m.dashboard_dataflow_size()}:</span>
						<span class="ml-1">{message.size} {m.dashboard_dataflow_bytes()}</span>
					</div>
					<div class="bg-white rounded p-2 font-mono text-sm break-all">
						{message.content}
					</div>
				</div>
			{/each}
		</div>
	</div>
</div>

