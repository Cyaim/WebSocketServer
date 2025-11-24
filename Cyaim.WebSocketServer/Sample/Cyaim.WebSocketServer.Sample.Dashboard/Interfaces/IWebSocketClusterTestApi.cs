using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Dashboard.Models;

namespace Cyaim.WebSocketServer.Sample.Dashboard.Interfaces
{
    /// <summary>
    /// WebSocket 集群测试接口
    /// WebSocket Cluster Test API Interface
    /// </summary>
    public interface IWebSocketClusterTestApi
    {
        #region 集群管理 / Cluster Management

        /// <summary>
        /// 获取集群概览
        /// Get cluster overview
        /// </summary>
        /// <returns>集群概览信息 / Cluster overview information</returns>
        Task<ApiResponse<ClusterOverview>> GetClusterOverviewAsync();

        /// <summary>
        /// 获取所有节点列表
        /// Get all nodes list
        /// </summary>
        /// <returns>节点状态列表 / Node status list</returns>
        Task<ApiResponse<List<NodeStatusInfo>>> GetNodesAsync();

        /// <summary>
        /// 获取指定节点信息
        /// Get specified node information
        /// </summary>
        /// <param name="nodeId">节点ID / Node ID</param>
        /// <returns>节点状态信息 / Node status information</returns>
        Task<ApiResponse<NodeStatusInfo?>> GetNodeAsync(string nodeId);

        /// <summary>
        /// 检查当前节点是否为领导者
        /// Check if current node is leader
        /// </summary>
        /// <returns>是否为领导者 / Is leader</returns>
        Task<ApiResponse<bool>> IsLeaderAsync();

        /// <summary>
        /// 获取当前节点ID
        /// Get current node ID
        /// </summary>
        /// <returns>节点ID / Node ID</returns>
        Task<ApiResponse<string>> GetCurrentNodeIdAsync();

        /// <summary>
        /// 获取用于负载均衡的最优节点
        /// Get optimal node for load balancing
        /// </summary>
        /// <returns>最优节点ID / Optimal node ID</returns>
        Task<ApiResponse<string>> GetOptimalNodeAsync();

        #endregion

        #region 客户端连接管理 / Client Connection Management

        /// <summary>
        /// 获取集群中全部客户端连接
        /// Get all client connections from cluster
        /// </summary>
        /// <param name="nodeId">可选的节点ID过滤器 / Optional node ID filter</param>
        /// <returns>客户端连接列表 / Client connection list</returns>
        Task<ApiResponse<List<ClientConnectionInfo>>> GetAllClientsAsync(string? nodeId = null);

        /// <summary>
        /// 获取指定节点的客户端连接
        /// Get client connections for specified node
        /// </summary>
        /// <param name="nodeId">节点ID / Node ID</param>
        /// <returns>客户端连接列表 / Client connection list</returns>
        Task<ApiResponse<List<ClientConnectionInfo>>> GetClientsByNodeAsync(string nodeId);

        /// <summary>
        /// 获取本地节点的客户端连接
        /// Get local node client connections
        /// </summary>
        /// <returns>客户端连接列表 / Client connection list</returns>
        Task<ApiResponse<List<ClientConnectionInfo>>> GetLocalClientsAsync();

        /// <summary>
        /// 获取指定连接的信息
        /// Get specified connection information
        /// </summary>
        /// <param name="connectionId">连接ID / Connection ID</param>
        /// <returns>客户端连接信息 / Client connection information</returns>
        Task<ApiResponse<ClientConnectionInfo>> GetClientAsync(string connectionId);

        /// <summary>
        /// 获取连接总数
        /// Get total connection count
        /// </summary>
        /// <returns>连接总数 / Total connection count</returns>
        Task<ApiResponse<int>> GetTotalConnectionCountAsync();

        /// <summary>
        /// 获取本地连接数
        /// Get local connection count
        /// </summary>
        /// <returns>本地连接数 / Local connection count</returns>
        Task<ApiResponse<int>> GetLocalConnectionCountAsync();

        /// <summary>
        /// 获取指定节点的连接数
        /// Get connection count for specified node
        /// </summary>
        /// <param name="nodeId">节点ID / Node ID</param>
        /// <returns>连接数 / Connection count</returns>
        Task<ApiResponse<int>> GetNodeConnectionCountAsync(string nodeId);

        #endregion

        #region 消息发送 / Message Sending

        /// <summary>
        /// 发送文本消息到指定连接
        /// Send text message to specified connection
        /// </summary>
        /// <param name="connectionId">连接ID / Connection ID</param>
        /// <param name="content">消息内容 / Message content</param>
        /// <returns>发送结果 / Send result</returns>
        Task<ApiResponse<bool>> SendTextMessageAsync(string connectionId, string content);

        /// <summary>
        /// 发送二进制消息到指定连接
        /// Send binary message to specified connection
        /// </summary>
        /// <param name="connectionId">连接ID / Connection ID</param>
        /// <param name="content">消息内容（Base64编码）/ Message content (Base64 encoded)</param>
        /// <returns>发送结果 / Send result</returns>
        Task<ApiResponse<bool>> SendBinaryMessageAsync(string connectionId, string content);

        /// <summary>
        /// 发送消息到指定连接（支持文本和二进制）
        /// Send message to specified connection (supports text and binary)
        /// </summary>
        /// <param name="request">发送消息请求 / Send message request</param>
        /// <returns>发送结果 / Send result</returns>
        Task<ApiResponse<bool>> SendMessageAsync(SendMessageRequest request);

        /// <summary>
        /// 批量发送消息到多个连接
        /// Send messages to multiple connections
        /// </summary>
        /// <param name="requests">发送消息请求列表 / Send message request list</param>
        /// <returns>发送结果列表（每个连接一个结果）/ Send result list (one result per connection)</returns>
        Task<ApiResponse<List<KeyValuePair<string, bool>>>> SendMessagesAsync(List<SendMessageRequest> requests);

        /// <summary>
        /// 广播消息到所有连接
        /// Broadcast message to all connections
        /// </summary>
        /// <param name="content">消息内容 / Message content</param>
        /// <param name="messageType">消息类型（Text/Binary）/ Message type (Text/Binary)</param>
        /// <returns>发送成功的连接数 / Number of successful sends</returns>
        Task<ApiResponse<int>> BroadcastMessageAsync(string content, string messageType = "Text");

        /// <summary>
        /// 广播消息到指定节点的所有连接
        /// Broadcast message to all connections on specified node
        /// </summary>
        /// <param name="nodeId">节点ID / Node ID</param>
        /// <param name="content">消息内容 / Message content</param>
        /// <param name="messageType">消息类型（Text/Binary）/ Message type (Text/Binary)</param>
        /// <returns>发送成功的连接数 / Number of successful sends</returns>
        Task<ApiResponse<int>> BroadcastMessageToNodeAsync(string nodeId, string content, string messageType = "Text");

        #endregion

        #region 统计信息 / Statistics

        /// <summary>
        /// 获取带宽统计信息
        /// Get bandwidth statistics
        /// </summary>
        /// <returns>带宽统计信息 / Bandwidth statistics</returns>
        Task<ApiResponse<BandwidthStatistics>> GetBandwidthStatisticsAsync();

        /// <summary>
        /// 获取指定连接的统计信息
        /// Get statistics for specified connection
        /// </summary>
        /// <param name="connectionId">连接ID / Connection ID</param>
        /// <returns>连接统计信息 / Connection statistics</returns>
        Task<ApiResponse<ClientConnectionInfo>> GetConnectionStatisticsAsync(string connectionId);

        /// <summary>
        /// 获取所有连接的统计信息
        /// Get statistics for all connections
        /// </summary>
        /// <returns>连接统计信息列表 / Connection statistics list</returns>
        Task<ApiResponse<List<ClientConnectionInfo>>> GetAllConnectionStatisticsAsync();

        #endregion

        #region 连接路由 / Connection Routing

        /// <summary>
        /// 获取连接路由表
        /// Get connection routing table
        /// </summary>
        /// <returns>连接ID到节点ID的映射 / Connection ID to Node ID mapping</returns>
        Task<ApiResponse<Dictionary<string, string>>> GetConnectionRoutesAsync();

        /// <summary>
        /// 查询连接所在的节点
        /// Query which node a connection is on
        /// </summary>
        /// <param name="connectionId">连接ID / Connection ID</param>
        /// <returns>节点ID / Node ID</returns>
        Task<ApiResponse<string>> QueryConnectionNodeAsync(string connectionId);

        /// <summary>
        /// 获取指定节点的所有连接ID
        /// Get all connection IDs for specified node
        /// </summary>
        /// <param name="nodeId">节点ID / Node ID</param>
        /// <returns>连接ID列表 / Connection ID list</returns>
        Task<ApiResponse<List<string>>> GetNodeConnectionIdsAsync(string nodeId);

        #endregion

        #region 健康检查 / Health Check

        /// <summary>
        /// 检查集群健康状态
        /// Check cluster health status
        /// </summary>
        /// <returns>健康状态信息 / Health status information</returns>
        Task<ApiResponse<ClusterHealthStatus>> GetClusterHealthAsync();

        /// <summary>
        /// 检查节点健康状态
        /// Check node health status
        /// </summary>
        /// <param name="nodeId">节点ID / Node ID</param>
        /// <returns>健康状态信息 / Health status information</returns>
        Task<ApiResponse<NodeHealthStatus>> GetNodeHealthAsync(string nodeId);

        #endregion
    }

    /// <summary>
    /// 集群健康状态 / Cluster Health Status
    /// </summary>
    public class ClusterHealthStatus
    {
        /// <summary>
        /// 是否健康 / Is healthy
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// 总节点数 / Total nodes
        /// </summary>
        public int TotalNodes { get; set; }

        /// <summary>
        /// 健康节点数 / Healthy nodes
        /// </summary>
        public int HealthyNodes { get; set; }

        /// <summary>
        /// 不健康节点数 / Unhealthy nodes
        /// </summary>
        public int UnhealthyNodes { get; set; }

        /// <summary>
        /// 是否有领导者 / Has leader
        /// </summary>
        public bool HasLeader { get; set; }

        /// <summary>
        /// 总连接数 / Total connections
        /// </summary>
        public int TotalConnections { get; set; }

        /// <summary>
        /// 健康检查时间 / Health check time
        /// </summary>
        public DateTime CheckTime { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 详细信息 / Details
        /// </summary>
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// 节点健康状态 / Node Health Status
    /// </summary>
    public class NodeHealthStatus
    {
        /// <summary>
        /// 节点ID / Node ID
        /// </summary>
        public string NodeId { get; set; } = string.Empty;

        /// <summary>
        /// 是否健康 / Is healthy
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// 是否连接 / Is connected
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// 连接数 / Connection count
        /// </summary>
        public int ConnectionCount { get; set; }

        /// <summary>
        /// 是否为领导者 / Is leader
        /// </summary>
        public bool IsLeader { get; set; }

        /// <summary>
        /// 响应时间（毫秒）/ Response time (milliseconds)
        /// </summary>
        public long ResponseTimeMs { get; set; }

        /// <summary>
        /// 健康检查时间 / Health check time
        /// </summary>
        public DateTime CheckTime { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 错误信息 / Error message
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}

