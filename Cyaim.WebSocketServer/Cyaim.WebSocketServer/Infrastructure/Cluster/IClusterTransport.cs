using System;
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

