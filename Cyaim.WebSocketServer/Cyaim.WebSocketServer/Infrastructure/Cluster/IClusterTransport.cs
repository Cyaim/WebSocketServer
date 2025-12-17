using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Cluster transport interface for inter-node communication
    /// 集群节点间通信传输接口
    /// </summary>
    public interface IClusterTransport : IDisposable
    {
        /// <summary>
        /// Start the transport service
        /// 启动传输服务
        /// </summary>
        Task StartAsync();

        /// <summary>
        /// Stop the transport service
        /// 停止传输服务
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Send message to specific node
        /// 向指定节点发送消息
        /// </summary>
        /// <param name="nodeId">Target node ID / 目标节点 ID</param>
        /// <param name="message">Message to send / 要发送的消息</param>
        Task SendAsync(string nodeId, ClusterMessage message);

        /// <summary>
        /// Broadcast message to all nodes
        /// 向所有节点广播消息
        /// </summary>
        /// <param name="message">Message to broadcast / 要广播的消息</param>
        Task BroadcastAsync(ClusterMessage message);

        /// <summary>
        /// Event triggered when message received
        /// 消息接收时触发的事件
        /// </summary>
        event EventHandler<ClusterMessageEventArgs> MessageReceived;

        /// <summary>
        /// Event triggered when node connected
        /// 节点连接时触发的事件
        /// </summary>
        event EventHandler<ClusterNodeEventArgs> NodeConnected;

        /// <summary>
        /// Event triggered when node disconnected
        /// 节点断开连接时触发的事件
        /// </summary>
        event EventHandler<ClusterNodeEventArgs> NodeDisconnected;

        /// <summary>
        /// Check if node is connected
        /// 检查节点是否已连接
        /// </summary>
        /// <param name="nodeId">Node ID to check / 要检查的节点 ID</param>
        /// <returns>True if connected, false otherwise / 已连接返回 true，否则返回 false</returns>
        bool IsNodeConnected(string nodeId);

        /// <summary>
        /// Store connection route information (optional, only for transports that support it)
        /// 存储连接路由信息（可选，仅支持此功能的传输层实现）
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="nodeId">Node ID where connection is located / 连接所在的节点 ID</param>
        /// <param name="metadata">Optional connection metadata / 可选的连接元数据</param>
        /// <returns>True if storage is supported and successful, false otherwise / 如果支持存储且成功返回 true，否则返回 false</returns>
        Task<bool> StoreConnectionRouteAsync(string connectionId, string nodeId, Dictionary<string, string> metadata = null);

        /// <summary>
        /// Get connection route information (optional, only for transports that support it)
        /// 获取连接路由信息（可选，仅支持此功能的传输层实现）
        /// </summary>
        /// <param name="connectionId">Connection ID to query / 要查询的连接 ID</param>
        /// <returns>Node ID where connection is located, or null if not found or not supported / 连接所在的节点 ID，如果未找到或不支持则返回 null</returns>
        Task<string> GetConnectionRouteAsync(string connectionId);

        /// <summary>
        /// Remove connection route information (optional, only for transports that support it)
        /// 删除连接路由信息（可选，仅支持此功能的传输层实现）
        /// </summary>
        /// <param name="connectionId">Connection ID to remove / 要删除的连接 ID</param>
        /// <returns>True if removal is supported and successful, false otherwise / 如果支持删除且成功返回 true，否则返回 false</returns>
        Task<bool> RemoveConnectionRouteAsync(string connectionId);

        /// <summary>
        /// Refresh connection route expiration (optional, only for transports that support it)
        /// 刷新连接路由过期时间（可选，仅支持此功能的传输层实现）
        /// </summary>
        /// <param name="connectionId">Connection ID to refresh / 要刷新的连接 ID</param>
        /// <param name="nodeId">Node ID where connection is located / 连接所在的节点 ID</param>
        /// <returns>True if refresh is supported and successful, false otherwise / 如果支持刷新且成功返回 true，否则返回 false</returns>
        Task<bool> RefreshConnectionRouteAsync(string connectionId, string nodeId);
    }

    /// <summary>
    /// Cluster message event arguments
    /// 集群消息事件参数
    /// </summary>
    public class ClusterMessageEventArgs : EventArgs
    {
        /// <summary>
        /// Source node ID / 源节点 ID
        /// </summary>
        public string FromNodeId { get; set; }

        /// <summary>
        /// Cluster message / 集群消息
        /// </summary>
        public ClusterMessage Message { get; set; }
    }

    /// <summary>
    /// Cluster node event arguments
    /// 集群节点事件参数
    /// </summary>
    public class ClusterNodeEventArgs : EventArgs
    {
        /// <summary>
        /// Node ID / 节点 ID
        /// </summary>
        public string NodeId { get; set; }
    }
}

