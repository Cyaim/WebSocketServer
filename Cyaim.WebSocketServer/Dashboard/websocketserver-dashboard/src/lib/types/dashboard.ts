/**
 * Dashboard TypeScript type definitions
 * Dashboard TypeScript 类型定义
 */

export interface NodeStatusInfo {
	nodeId: string;
	address?: string;
	port?: number;
	state: string;
	currentTerm?: number;
	isLeader: boolean;
	leaderId?: string;
	connectionCount: number;
	isConnected: boolean;
	lastHeartbeat?: string;
	logLength?: number;
}

export interface ClientConnectionInfo {
	connectionId: string;
	nodeId: string;
	remoteIpAddress?: string;
	remotePort?: number;
	state: string;
	connectedAt?: string;
	endpoint?: string;
	bytesSent: number;
	bytesReceived: number;
	messagesSent: number;
	messagesReceived: number;
}

export interface BandwidthStatistics {
	totalBytesSent: number;
	totalBytesReceived: number;
	bytesSentPerSecond: number;
	bytesReceivedPerSecond: number;
	totalMessagesSent: number;
	totalMessagesReceived: number;
	messagesSentPerSecond: number;
	messagesReceivedPerSecond: number;
	timestamp: string;
}

export interface ClusterOverview {
	totalNodes: number;
	connectedNodes: number;
	totalConnections: number;
	localConnections: number;
	currentNodeId: string;
	isCurrentNodeLeader: boolean;
	nodes: NodeStatusInfo[];
}

export interface DataFlowMessage {
	messageId: string;
	connectionId: string;
	nodeId: string;
	direction: 'Inbound' | 'Outbound';
	messageType: 'Text' | 'Binary';
	size: number;
	content: string;
	timestamp: string;
}

export interface SendMessageRequest {
	connectionId: string;
	content: string;
	messageType?: 'Text' | 'Binary';
}

export interface ApiResponse<T> {
	success: boolean;
	data?: T;
	error?: string;
	timestamp?: string;
}

