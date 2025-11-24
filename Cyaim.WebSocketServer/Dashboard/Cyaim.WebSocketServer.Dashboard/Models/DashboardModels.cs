using System;
using System.Collections.Generic;

namespace Cyaim.WebSocketServer.Dashboard.Models
{
    /// <summary>
    /// Dashboard response models
    /// Dashboard 响应模型
    /// </summary>

    /// <summary>
    /// Node status information / 节点状态信息
    /// </summary>
    public class NodeStatusInfo
    {
        /// <summary>
        /// Node ID / 节点 ID
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
        /// Raft state (Follower, Candidate, Leader) / Raft 状态（跟随者、候选者、领导者）
        /// </summary>
        public string State { get; set; }

        /// <summary>
        /// Current term / 当前任期
        /// </summary>
        public long CurrentTerm { get; set; }

        /// <summary>
        /// Is this node the leader / 此节点是否为领导者
        /// </summary>
        public bool IsLeader { get; set; }

        /// <summary>
        /// Current leader node ID / 当前领导者节点 ID
        /// </summary>
        public string LeaderId { get; set; }

        /// <summary>
        /// Connection count on this node / 此节点上的连接数
        /// </summary>
        public int ConnectionCount { get; set; }

        /// <summary>
        /// Is node connected / 节点是否已连接
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// Last heartbeat time / 最后心跳时间
        /// </summary>
        public DateTime? LastHeartbeat { get; set; }

        /// <summary>
        /// Log length / 日志长度
        /// </summary>
        public long LogLength { get; set; }
    }

    /// <summary>
    /// Client connection information / 客户端连接信息
    /// </summary>
    public class ClientConnectionInfo
    {
        /// <summary>
        /// Connection ID / 连接 ID
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// Node ID where connection is located / 连接所在的节点 ID
        /// </summary>
        public string NodeId { get; set; }

        /// <summary>
        /// Remote IP address / 远程 IP 地址
        /// </summary>
        public string RemoteIpAddress { get; set; }

        /// <summary>
        /// Remote port / 远程端口
        /// </summary>
        public int RemotePort { get; set; }

        /// <summary>
        /// WebSocket state / WebSocket 状态
        /// </summary>
        public string State { get; set; }

        /// <summary>
        /// Connected time / 连接时间
        /// </summary>
        public DateTime? ConnectedAt { get; set; }

        /// <summary>
        /// Endpoint path / 端点路径
        /// </summary>
        public string Endpoint { get; set; }

        /// <summary>
        /// Bytes sent / 发送的字节数
        /// </summary>
        public long BytesSent { get; set; }

        /// <summary>
        /// Bytes received / 接收的字节数
        /// </summary>
        public long BytesReceived { get; set; }

        /// <summary>
        /// Messages sent / 发送的消息数
        /// </summary>
        public long MessagesSent { get; set; }

        /// <summary>
        /// Messages received / 接收的消息数
        /// </summary>
        public long MessagesReceived { get; set; }
    }

    /// <summary>
    /// Network bandwidth statistics / 网络带宽统计
    /// </summary>
    public class BandwidthStatistics
    {
        /// <summary>
        /// Total bytes sent / 总发送字节数
        /// </summary>
        public long TotalBytesSent { get; set; }

        /// <summary>
        /// Total bytes received / 总接收字节数
        /// </summary>
        public long TotalBytesReceived { get; set; }

        /// <summary>
        /// Bytes sent per second / 每秒发送字节数
        /// </summary>
        public double BytesSentPerSecond { get; set; }

        /// <summary>
        /// Bytes received per second / 每秒接收字节数
        /// </summary>
        public double BytesReceivedPerSecond { get; set; }

        /// <summary>
        /// Total messages sent / 总发送消息数
        /// </summary>
        public long TotalMessagesSent { get; set; }

        /// <summary>
        /// Total messages received / 总接收消息数
        /// </summary>
        public long TotalMessagesReceived { get; set; }

        /// <summary>
        /// Messages sent per second / 每秒发送消息数
        /// </summary>
        public double MessagesSentPerSecond { get; set; }

        /// <summary>
        /// Messages received per second / 每秒接收消息数
        /// </summary>
        public double MessagesReceivedPerSecond { get; set; }

        /// <summary>
        /// Statistics timestamp / 统计时间戳
        /// </summary>
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Cluster overview / 集群概览
    /// </summary>
    public class ClusterOverview
    {
        /// <summary>
        /// Total nodes / 总节点数
        /// </summary>
        public int TotalNodes { get; set; }

        /// <summary>
        /// Connected nodes / 已连接节点数
        /// </summary>
        public int ConnectedNodes { get; set; }

        /// <summary>
        /// Total connections / 总连接数
        /// </summary>
        public int TotalConnections { get; set; }

        /// <summary>
        /// Local connections / 本地连接数
        /// </summary>
        public int LocalConnections { get; set; }

        /// <summary>
        /// Current node ID / 当前节点 ID
        /// </summary>
        public string CurrentNodeId { get; set; }

        /// <summary>
        /// Is current node leader / 当前节点是否为领导者
        /// </summary>
        public bool IsCurrentNodeLeader { get; set; }

        /// <summary>
        /// Node status list / 节点状态列表
        /// </summary>
        public List<NodeStatusInfo> Nodes { get; set; } = new List<NodeStatusInfo>();
    }

    /// <summary>
    /// Data flow message / 数据流消息
    /// </summary>
    public class DataFlowMessage
    {
        /// <summary>
        /// Message ID / 消息 ID
        /// </summary>
        public string MessageId { get; set; }

        /// <summary>
        /// Connection ID / 连接 ID
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// Node ID / 节点 ID
        /// </summary>
        public string NodeId { get; set; }

        /// <summary>
        /// Direction (Inbound/Outbound) / 方向（入站/出站）
        /// </summary>
        public string Direction { get; set; }

        /// <summary>
        /// Message type (Text/Binary) / 消息类型（文本/二进制）
        /// </summary>
        public string MessageType { get; set; }

        /// <summary>
        /// Message size in bytes / 消息大小（字节）
        /// </summary>
        public int Size { get; set; }

        /// <summary>
        /// Message content (truncated if too large) / 消息内容（如果太大则截断）
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// Timestamp / 时间戳
        /// </summary>
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Send message request / 发送消息请求
    /// </summary>
    public class SendMessageRequest
    {
        /// <summary>
        /// Connection ID / 连接 ID
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// Message content / 消息内容
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// Message type (Text/Binary) / 消息类型（文本/二进制）
        /// </summary>
        public string MessageType { get; set; } = "Text";
    }

    /// <summary>
    /// API response wrapper / API 响应包装器
    /// </summary>
    public class ApiResponse<T>
    {
        /// <summary>
        /// Success flag / 成功标志
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Response data / 响应数据
        /// </summary>
        public T Data { get; set; }

        /// <summary>
        /// Error message / 错误消息
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// Timestamp / 时间戳
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}

