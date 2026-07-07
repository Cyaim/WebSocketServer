<script lang="ts">
	import { onMount } from 'svelte';
	import { getRecentMessages } from '$lib/api/dashboard';
	import type { DataFlowMessage } from '$lib/types/dashboard';
	import { m } from '$lib/paraglide/messages';

	let messages: DataFlowMessage[] = $state([]);
	let maxMessages = 100;
	let autoScroll = $state(true);
	let error = $state<string | null>(null);
	let container: HTMLElement;
	// Incremental poll cursor: the newest message id we have seen. 增量轮询游标：已见到的最新消息 id。
	let sinceId = 0;

	const poll = async () => {
		try {
			const response = await getRecentMessages(sinceId, maxMessages);
			if (response.success && response.data) {
				if (response.data.length > 0) {
					sinceId = Math.max(...response.data.map((x) => Number(x.messageId)));
					// newest first / 最新在前
					messages = [...response.data.reverse(), ...messages].slice(0, maxMessages);
					if (autoScroll && container) {
						container.scrollTop = 0;
					}
				}
				error = null;
			} else {
				error = response.error || 'Failed to fetch data flow';
			}
		} catch (err) {
			error = err instanceof Error ? err.message : 'Unknown error';
		}
	};

	onMount(() => {
		poll();
		const interval = setInterval(poll, 1000);
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

	{#if error}
		<div class="bg-red-50 border-l-4 border-red-500 text-red-700 p-4 rounded">{error}</div>
	{/if}

	<div
		bind:this={container}
		class="bg-white rounded-lg shadow overflow-y-auto"
		style="max-height: calc(100vh - 300px);"
	>
		<div class="p-4 space-y-4">
			{#each messages as message (message.messageId)}
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
						{#if message.messageType === 'Text'}
							<span class="px-2 py-1 text-xs bg-gray-200 text-gray-700 rounded-full">{m.dashboard_dataflow_text()}</span>
						{:else if message.messageType === 'Binary'}
							<span class="px-2 py-1 text-xs bg-gray-200 text-gray-700 rounded-full">{m.dashboard_dataflow_binary()}</span>
						{/if}
						<span class="px-2 py-1 text-xs bg-gray-100 text-gray-500 rounded-full">{message.nodeId}</span>
						<span class="ml-auto text-xs text-gray-500">
							{new Date(message.timestamp).toLocaleTimeString()}
						</span>
					</div>
					<div class="text-sm text-gray-600">
						<span class="font-semibold">{m.dashboard_dataflow_connection()}:</span>
						<code class="ml-1 text-purple-600">{message.connectionId}</code>
						<span class="ml-4 font-semibold">{m.dashboard_dataflow_size()}:</span>
						<span class="ml-1">{message.size} {m.dashboard_dataflow_bytes()}</span>
					</div>
					{#if message.content}
						<div class="bg-white rounded p-2 mt-2 font-mono text-sm break-all">
							{message.content}
						</div>
					{/if}
				</div>
			{:else}
				<div class="text-center text-gray-400 py-8">{m.dashboard_common_loading()}</div>
			{/each}
		</div>
	</div>
</div>
