/**
 * Dashboard API client — talks to the Cyaim.WebSocketServer Dashboard controllers.
 * Dashboard API 客户端 —— 对接服务端 Dashboard 控制器。
 *
 * Backend routes (see Cyaim.WebSocketServer.Dashboard/Controllers):
 *   GET    /ws_server/api/cluster/overview
 *   GET    /ws_server/api/cluster/nodes
 *   GET    /ws_server/api/client[?nodeId=]
 *   DELETE /ws_server/api/client/{connectionId}
 *   GET    /ws_server/api/statistics/bandwidth
 *   GET    /ws_server/api/statistics/timeseries
 *   GET    /ws_server/api/messages/recent?sinceId=&max=
 *   POST   /ws_server/api/messages/send
 *   POST   /ws_server/api/messages/broadcast
 */

import type {
	ApiResponse,
	BandwidthStatistics,
	ClientConnectionInfo,
	ClusterOverview,
	DataFlowMessage,
	MetricsSample,
	NodeStatusInfo,
	SendMessageRequest
} from '../types/dashboard';

// The controllers are mounted at the app root, independent of the dashboard UI path.
// 控制器挂在应用根路径，与 Dashboard UI 路径无关。
const API_BASE_URL = '/ws_server/api';

/**
 * Fetch API with error handling
 * 带错误处理的 API 请求
 */
async function fetchApi<T>(url: string, options?: RequestInit): Promise<ApiResponse<T>> {
	try {
		const response = await fetch(`${API_BASE_URL}${url}`, {
			...options,
			headers: {
				'Content-Type': 'application/json',
				...options?.headers
			}
		});

		// The backend wraps errors in ApiResponse too; prefer its body when parseable.
		// 后端错误也用 ApiResponse 包装；能解析时优先用响应体。
		const data = (await response.json().catch(() => null)) as ApiResponse<T> | null;
		if (data && typeof data.success === 'boolean') {
			return data;
		}
		if (!response.ok) {
			return {
				success: false,
				error: `HTTP ${response.status}: ${response.statusText}`
			};
		}
		return { success: false, error: 'Malformed response' };
	} catch (error) {
		return {
			success: false,
			error: error instanceof Error ? error.message : 'Unknown error'
		};
	}
}

/**
 * Get cluster overview
 * 获取集群概览
 */
export async function getClusterOverview(): Promise<ApiResponse<ClusterOverview>> {
	return fetchApi<ClusterOverview>('/cluster/overview');
}

/**
 * Get node status list
 * 获取节点状态列表
 */
export async function getNodes(): Promise<ApiResponse<NodeStatusInfo[]>> {
	return fetchApi<NodeStatusInfo[]>('/cluster/nodes');
}

/**
 * Get client connections
 * 获取客户端连接
 */
export async function getClients(nodeId?: string): Promise<ApiResponse<ClientConnectionInfo[]>> {
	const url = nodeId ? `/client?nodeId=${encodeURIComponent(nodeId)}` : '/client';
	return fetchApi<ClientConnectionInfo[]>(url);
}

/**
 * Disconnect (close) a client connection — management operation.
 * 断开（关闭）一个客户端连接 —— 管理操作。
 */
export async function disconnectClient(connectionId: string): Promise<ApiResponse<boolean>> {
	return fetchApi<boolean>(`/client/${encodeURIComponent(connectionId)}`, { method: 'DELETE' });
}

/**
 * Get bandwidth statistics
 * 获取带宽统计信息
 */
export async function getBandwidth(): Promise<ApiResponse<BandwidthStatistics>> {
	return fetchApi<BandwidthStatistics>('/statistics/bandwidth');
}

/**
 * Get the per-second metrics time series for charts
 * 获取每秒指标时序（用于图表）
 */
export async function getTimeSeries(): Promise<ApiResponse<MetricsSample[]>> {
	return fetchApi<MetricsSample[]>('/statistics/timeseries');
}

/**
 * Get recent data-flow message events (incremental poll via sinceId)
 * 获取最近数据流消息事件（用 sinceId 增量轮询）
 */
export async function getRecentMessages(
	sinceId = 0,
	max = 200
): Promise<ApiResponse<DataFlowMessage[]>> {
	return fetchApi<DataFlowMessage[]>(`/messages/recent?sinceId=${sinceId}&max=${max}`);
}

/**
 * Send message to connection
 * 向连接发送消息
 */
export async function sendMessage(request: SendMessageRequest): Promise<ApiResponse<boolean>> {
	return fetchApi<boolean>('/messages/send', {
		method: 'POST',
		body: JSON.stringify(request)
	});
}

/**
 * Broadcast a message to all connections — management operation.
 * 向所有连接广播消息 —— 管理操作。
 */
export async function broadcastMessage(
	content: string,
	messageType: 'Text' | 'Binary' = 'Text'
): Promise<ApiResponse<number>> {
	return fetchApi<number>('/messages/broadcast', {
		method: 'POST',
		body: JSON.stringify({ content, messageType })
	});
}
