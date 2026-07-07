<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { getBandwidth } from '$lib/api/dashboard';
	import type { BandwidthStatistics } from '$lib/types/dashboard';
	import { m } from '$lib/paraglide/messages';

	let bandwidth: BandwidthStatistics | null = null;
	let loading = true;
	let error: string | null = null;
	let refreshInterval: ReturnType<typeof setInterval> | null = null;
	let chartData = {
		labels: [] as string[],
		sent: [] as number[],
		received: [] as number[]
	};

	const fetchBandwidth = async () => {
		try {
			loading = true;
			const response = await getBandwidth();
			if (response.success && response.data) {
				bandwidth = response.data;
				error = null;

				// Update chart data
				const now = new Date().toLocaleTimeString();
				chartData.labels.push(now);
				chartData.sent.push(bandwidth.bytesSentPerSecond);
				chartData.received.push(bandwidth.bytesReceivedPerSecond);

				// Keep only last 20 data points
				if (chartData.labels.length > 20) {
					chartData.labels.shift();
					chartData.sent.shift();
					chartData.received.shift();
				}
			} else {
				error = response.error || 'Failed to fetch bandwidth';
			}
		} catch (err) {
			error = err instanceof Error ? err.message : 'Unknown error';
		} finally {
			loading = false;
		}
	};

	onMount(() => {
		fetchBandwidth();
		refreshInterval = setInterval(fetchBandwidth, 1000);
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
	<h2 class="text-2xl font-bold text-gray-800">{m.dashboard_bandwidth_title()}</h2>

	{#if loading && !bandwidth}
		<div class="text-center py-12">
			<p class="text-gray-600">{m.dashboard_common_loading()}</p>
		</div>
	{:else if error}
		<div class="bg-red-50 border border-red-200 rounded-lg p-4">
			<p class="text-red-800">{m.dashboard_common_error()}: {error}</p>
		</div>
	{:else if bandwidth}
		<!-- Statistics Cards -->
		<div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_bandwidth_totalSent()}</div>
				<div class="text-2xl font-bold text-purple-600">{formatBytes(bandwidth.totalBytesSent)}</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_bandwidth_totalReceived()}</div>
				<div class="text-2xl font-bold text-green-600">
					{formatBytes(bandwidth.totalBytesReceived)}
				</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_bandwidth_sentPerSec()}</div>
				<div class="text-2xl font-bold text-blue-600">
					{formatBytes(bandwidth.bytesSentPerSecond)}/s
				</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_bandwidth_receivedPerSec()}</div>
				<div class="text-2xl font-bold text-indigo-600">
					{formatBytes(bandwidth.bytesReceivedPerSecond)}/s
				</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_bandwidth_messagesSent()}</div>
				<div class="text-2xl font-bold text-purple-600">{bandwidth.totalMessagesSent}</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_bandwidth_messagesReceived()}</div>
				<div class="text-2xl font-bold text-green-600">{bandwidth.totalMessagesReceived}</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_bandwidth_msgSentPerSec()}</div>
				<div class="text-2xl font-bold text-blue-600">
					{bandwidth.messagesSentPerSecond.toFixed(2)}
				</div>
			</div>
			<div class="bg-white rounded-lg shadow p-6">
				<div class="text-sm text-gray-600 mb-2">{m.dashboard_bandwidth_msgReceivedPerSec()}</div>
				<div class="text-2xl font-bold text-indigo-600">
					{bandwidth.messagesReceivedPerSecond.toFixed(2)}
				</div>
			</div>
		</div>

		<!-- Simple Chart -->
		<div class="bg-white rounded-lg shadow p-6">
			<h3 class="text-lg font-semibold text-gray-800 mb-4">{m.dashboard_bandwidth_overTime()}</h3>
			<div class="flex items-end gap-2 h-64">
				{#each chartData.labels as label, index}
					<div class="flex-1 flex flex-col justify-end gap-1 min-h-[20px]">
						<div
							class="bg-purple-600 rounded-t"
							style="height: {Math.min(100, (chartData.sent[index] / 1024 / 1024) * 10)}%"
						></div>
						<div
							class="bg-green-600 rounded-t"
							style="height: {Math.min(100, (chartData.received[index] / 1024 / 1024) * 10)}%"
						></div>
					</div>
				{/each}
			</div>
			<div class="flex gap-6 justify-center mt-4">
				<div class="flex items-center gap-2">
					<div class="w-4 h-4 bg-purple-600 rounded"></div>
					<span class="text-sm text-gray-600">Sent</span>
				</div>
				<div class="flex items-center gap-2">
					<div class="w-4 h-4 bg-green-600 rounded"></div>
					<span class="text-sm text-gray-600">Received</span>
				</div>
			</div>
		</div>
	{/if}
</div>

