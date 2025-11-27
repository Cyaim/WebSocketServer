using System;
using System.Text.Json.Serialization;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Cluster message types
    /// 集群消息类型
    /// </summary>
    public enum ClusterMessageType
    {
        // Raft protocol messages / Raft 协议消息
        /// <summary>
        /// Request vote message / 请求投票消息
        /// </summary>
        RequestVote,
        /// <summary>
        /// Request vote response / 请求投票响应
        /// </summary>
        RequestVoteResponse,
        /// <summary>
        /// Append entries message / 追加条目消息
        /// </summary>
        AppendEntries,
        /// <summary>
        /// Append entries response / 追加条目响应
        /// </summary>
        AppendEntriesResponse,
        /// <summary>
        /// Heartbeat message / 心跳消息
        /// </summary>
        Heartbeat,

        // WebSocket forwarding messages / WebSocket 转发消息
        /// <summary>
        /// Forward WebSocket message / 转发 WebSocket 消息
        /// </summary>
        ForwardWebSocketMessage,
        /// <summary>
        /// Forward WebSocket response / 转发 WebSocket 响应
        /// </summary>
        ForwardWebSocketResponse,
        /// <summary>
        /// Register WebSocket connection / 注册 WebSocket 连接
        /// </summary>
        RegisterWebSocketConnection,
        /// <summary>
        /// Unregister WebSocket connection / 注销 WebSocket 连接
        /// </summary>
        UnregisterWebSocketConnection,
        /// <summary>
        /// Query WebSocket connection / 查询 WebSocket 连接
        /// </summary>
        QueryWebSocketConnection,

        // Cluster management messages / 集群管理消息
        /// <summary>
        /// Node join message / 节点加入消息
        /// </summary>
        NodeJoin,
        /// <summary>
        /// Node leave message / 节点离开消息
        /// </summary>
        NodeLeave,
        /// <summary>
        /// Node info message / 节点信息消息
        /// </summary>
        NodeInfo,
        /// <summary>
        /// Cluster state sync message / 集群状态同步消息
        /// </summary>
        ClusterStateSync
    }

    /// <summary>
    /// Cluster message
    /// 集群消息
    /// </summary>
    public class ClusterMessage
    {
        /// <summary>
        /// Message type / 消息类型
        /// </summary>
        [JsonPropertyName("type")]
        public ClusterMessageType Type { get; set; }

        /// <summary>
        /// Source node ID / 源节点 ID
        /// </summary>
        [JsonPropertyName("from")]
        public string FromNodeId { get; set; }

        /// <summary>
        /// Target node ID (null for broadcast) / 目标节点 ID（null 表示广播）
        /// </summary>
        [JsonPropertyName("to")]
        public string ToNodeId { get; set; }

        /// <summary>
        /// Message ID for correlation / 用于关联的消息 ID
        /// </summary>
        [JsonPropertyName("id")]
        public string MessageId { get; set; }

        /// <summary>
        /// Timestamp / 时间戳
        /// </summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Message payload (JSON serialized) / 消息负载（JSON 序列化）
        /// </summary>
        [JsonPropertyName("payload")]
        public string Payload { get; set; }

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public ClusterMessage()
        {
            MessageId = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Raft request vote message
    /// Raft 请求投票消息
    /// </summary>
    public class RequestVoteMessage
    {
        /// <summary>
        /// Current term / 当前任期
        /// </summary>
        public long Term { get; set; }

        /// <summary>
        /// Candidate node ID / 候选节点 ID
        /// </summary>
        public string CandidateId { get; set; }

        /// <summary>
        /// Last log index / 最后日志索引
        /// </summary>
        public long LastLogIndex { get; set; }

        /// <summary>
        /// Last log term / 最后日志任期
        /// </summary>
        public long LastLogTerm { get; set; }
    }

    /// <summary>
    /// Raft request vote response
    /// Raft 请求投票响应
    /// </summary>
    public class RequestVoteResponseMessage
    {
        /// <summary>
        /// Current term / 当前任期
        /// </summary>
        public long Term { get; set; }

        /// <summary>
        /// Whether vote is granted / 是否授予投票
        /// </summary>
        public bool VoteGranted { get; set; }
    }

    /// <summary>
    /// Raft append entries message
    /// Raft 追加条目消息
    /// </summary>
    public class AppendEntriesMessage
    {
        /// <summary>
        /// Current term / 当前任期
        /// </summary>
        public long Term { get; set; }

        /// <summary>
        /// Leader node ID / 领导者节点 ID
        /// </summary>
        public string LeaderId { get; set; }

        /// <summary>
        /// Previous log index / 前一条日志索引
        /// </summary>
        public long PrevLogIndex { get; set; }

        /// <summary>
        /// Previous log term / 前一条日志任期
        /// </summary>
        public long PrevLogTerm { get; set; }

        /// <summary>
        /// Log entries to append / 要追加的日志条目
        /// </summary>
        public RaftLogEntry[] Entries { get; set; }

        /// <summary>
        /// Leader commit index / 领导者提交索引
        /// </summary>
        public long LeaderCommit { get; set; }
    }

    /// <summary>
    /// Raft append entries response
    /// Raft 追加条目响应
    /// </summary>
    public class AppendEntriesResponseMessage
    {
        /// <summary>
        /// Current term / 当前任期
        /// </summary>
        public long Term { get; set; }

        /// <summary>
        /// Whether operation succeeded / 操作是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Last log index / 最后日志索引
        /// </summary>
        public long LastLogIndex { get; set; }
    }

    /// <summary>
    /// Raft log entry
    /// Raft 日志条目
    /// </summary>
    public class RaftLogEntry
    {
        /// <summary>
        /// Log index / 日志索引
        /// </summary>
        public long Index { get; set; }

        /// <summary>
        /// Log term / 日志任期
        /// </summary>
        public long Term { get; set; }

        /// <summary>
        /// Command / 命令
        /// </summary>
        public string Command { get; set; }

        /// <summary>
        /// Data / 数据
        /// </summary>
        public string Data { get; set; }
    }

    /// <summary>
    /// WebSocket forward message
    /// WebSocket 转发消息
    /// </summary>
    public class ForwardWebSocketMessage
    {
        /// <summary>
        /// Connection ID / 连接 ID
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// Target node ID / 目标节点 ID
        /// </summary>
        public string TargetNodeId { get; set; }

        /// <summary>
        /// Message data / 消息数据
        /// </summary>
        public byte[] Data { get; set; }

        /// <summary>
        /// Message type (WebSocketMessageType as int) / 消息类型（WebSocketMessageType 的整数值）
        /// </summary>
        public int MessageType { get; set; }

        /// <summary>
        /// Endpoint / 端点
        /// </summary>
        public string Endpoint { get; set; }
    }

    /// <summary>
    /// WebSocket connection registration
    /// WebSocket 连接注册信息
    /// </summary>
    public class WebSocketConnectionRegistration
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
        /// Endpoint / 端点
        /// </summary>
        public string Endpoint { get; set; }

        /// <summary>
        /// Remote IP address / 远程 IP 地址
        /// </summary>
        public string RemoteIpAddress { get; set; }

        /// <summary>
        /// Remote port / 远程端口
        /// </summary>
        public int RemotePort { get; set; }

        /// <summary>
        /// Registration time / 注册时间
        /// </summary>
        public DateTime RegisteredAt { get; set; }
    }
}

