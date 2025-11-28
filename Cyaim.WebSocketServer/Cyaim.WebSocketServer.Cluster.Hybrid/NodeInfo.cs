using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Cyaim.WebSocketServer.Cluster.Hybrid
{
    /// <summary>
    /// Cluster node information stored in Redis
    /// 存储在 Redis 中的集群节点信息
    /// </summary>
    public class NodeInfo
    {
        /// <summary>
        /// Node ID / 节点 ID
        /// </summary>
        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; }

        /// <summary>
        /// Node address / 节点地址
        /// </summary>
        [JsonPropertyName("address")]
        public string Address { get; set; }

        /// <summary>
        /// Node port / 节点端口
        /// </summary>
        [JsonPropertyName("port")]
        public int Port { get; set; }

        /// <summary>
        /// WebSocket endpoint / WebSocket 端点
        /// </summary>
        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; }

        /// <summary>
        /// Current connection count / 当前连接数
        /// </summary>
        [JsonPropertyName("connectionCount")]
        public int ConnectionCount { get; set; }

        /// <summary>
        /// Maximum connections capacity / 最大连接容量
        /// </summary>
        [JsonPropertyName("maxConnections")]
        public int MaxConnections { get; set; }

        /// <summary>
        /// CPU usage percentage / CPU 使用率百分比
        /// </summary>
        [JsonPropertyName("cpuUsage")]
        public double CpuUsage { get; set; }

        /// <summary>
        /// Memory usage percentage / 内存使用率百分比
        /// </summary>
        [JsonPropertyName("memoryUsage")]
        public double MemoryUsage { get; set; }

        /// <summary>
        /// Last heartbeat timestamp / 最后心跳时间戳
        /// </summary>
        [JsonPropertyName("lastHeartbeat")]
        public DateTime LastHeartbeat { get; set; }

        /// <summary>
        /// Node registration timestamp / 节点注册时间戳
        /// </summary>
        [JsonPropertyName("registeredAt")]
        public DateTime RegisteredAt { get; set; }

        /// <summary>
        /// Node status / 节点状态
        /// </summary>
        [JsonPropertyName("status")]
        public NodeStatus Status { get; set; }

        /// <summary>
        /// Node metadata / 节点元数据
        /// </summary>
        [JsonPropertyName("metadata")]
        public Dictionary<string, string> Metadata { get; set; }

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public NodeInfo()
        {
            Metadata = new Dictionary<string, string>();
            Status = NodeStatus.Active;
            RegisteredAt = DateTime.UtcNow;
            LastHeartbeat = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Node status / 节点状态
    /// </summary>
    public enum NodeStatus
    {
        /// <summary>
        /// Node is active / 节点活跃
        /// </summary>
        Active,

        /// <summary>
        /// Node is draining (not accepting new connections) / 节点正在排空（不接受新连接）
        /// </summary>
        Draining,

        /// <summary>
        /// Node is offline / 节点离线
        /// </summary>
        Offline
    }
}
