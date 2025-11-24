/**
 * Dashboard API client
 * Dashboard API 客户端
 */

import type {
	ApiResponse,
	BandwidthStatistics,
	ClientConnectionInfo,
	ClusterOverview,
	NodeStatusInfo,
	SendMessageRequest
} from '../types/dashboard';

const API_BASE_URL = '/api/dashboard';

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

		if (!response.ok) {
			return {
				success: false,
				error: `HTTP ${response.status}: ${response.statusText}`
			};
		}

		const data = await response.json();
		return data as ApiResponse<T>;
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
	const url = nodeId ? `/clients?nodeId=${encodeURIComponent(nodeId)}` : '/clients';
	return fetchApi<ClientConnectionInfo[]>(url);
}

/**
 * Get bandwidth statistics
 * 获取带宽统计信息
 */
export async function getBandwidth(): Promise<ApiResponse<BandwidthStatistics>> {
	return fetchApi<BandwidthStatistics>('/bandwidth');
}

/**
 * Send message to connection
 * 向连接发送消息
 */
export async function sendMessage(
	request: SendMessageRequest
): Promise<ApiResponse<boolean>> {
	return fetchApi<boolean>('/send', {
		method: 'POST',
		body: JSON.stringify(request)
	});
}

