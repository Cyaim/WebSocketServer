using System;
using System.Net;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Cluster node information
    /// 集群节点信息
    /// </summary>
    public class ClusterNode
    {
        /// <summary>
        /// Node unique identifier / 节点唯一标识符
        /// </summary>
        public string NodeId { get; set; }

        /// <summary>
        /// Node address / 节点地址
        /// </summary>
        public string Address { get; set; }

        /// <summary>
        /// Node port / 节点端口
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Whether node is currently connected / 节点当前是否已连接
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// Last heartbeat time / 最后心跳时间
        /// </summary>
        public DateTime LastHeartbeat { get; set; }

        /// <summary>
        /// Node state in Raft protocol / Raft 协议中的节点状态
        /// </summary>
        public RaftNodeState State { get; set; }

        /// <summary>
        /// Current term (for Raft) / 当前任期（用于 Raft）
        /// </summary>
        public long CurrentTerm { get; set; }

        /// <summary>
        /// Raft log entries count / Raft 日志条目数量
        /// </summary>
        public long LogLength { get; set; }

        /// <summary>
        /// Full node address (protocol://address:port) / 完整节点地址（协议://地址:端口）
        /// </summary>
        public string FullAddress => $"{Protocol}://{Address}:{Port}";

        /// <summary>
        /// Transport protocol (ws, redis, rabbitmq) / 传输协议（ws, redis, rabbitmq）
        /// </summary>
        public string Protocol { get; set; } = "ws";

        /// <summary>
        /// Additional configuration for transport (e.g., Redis connection string, RabbitMQ connection)
        /// 传输的额外配置（例如：Redis 连接字符串、RabbitMQ 连接）
        /// </summary>
        public string TransportConfig { get; set; }
    }

    /// <summary>
    /// Raft node state
    /// Raft 节点状态
    /// </summary>
    public enum RaftNodeState
    {
        /// <summary>
        /// Follower state / 跟随者状态
        /// </summary>
        Follower,
        /// <summary>
        /// Candidate state / 候选者状态
        /// </summary>
        Candidate,
        /// <summary>
        /// Leader state / 领导者状态
        /// </summary>
        Leader
    }
}

