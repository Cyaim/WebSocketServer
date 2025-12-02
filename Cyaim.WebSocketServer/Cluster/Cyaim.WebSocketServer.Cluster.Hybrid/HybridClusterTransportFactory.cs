using System;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Cluster.Hybrid
{
    /// <summary>
    /// Factory for creating hybrid cluster transport
    /// 创建混合集群传输的工厂
    /// </summary>
    public class HybridClusterTransportFactory
    {
        /// <summary>
        /// Create hybrid cluster transport / 创建混合集群传输
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="loggerFactory">Logger factory for creating specific loggers / 用于创建特定 logger 的 logger factory</param>
        /// <param name="redisService">Redis service / Redis 服务</param>
        /// <param name="messageQueueService">Message queue service / 消息队列服务</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <param name="nodeAddress">Node address / 节点地址</param>
        /// <param name="nodePort">Node port / 节点端口</param>
        /// <param name="endpoint">WebSocket endpoint / WebSocket 端点</param>
        /// <param name="maxConnections">Maximum connections / 最大连接数</param>
        /// <param name="loadBalancingStrategy">Load balancing strategy / 负载均衡策略</param>
        /// <param name="nodeInfoProvider">Optional function to automatically get latest node info during heartbeat / 可选函数，用于在心跳时自动获取最新节点信息</param>
        /// <returns>Hybrid cluster transport instance / 混合集群传输实例</returns>
        public static IClusterTransport Create(
            ILogger<HybridClusterTransport> logger,
            ILoggerFactory loggerFactory,
            IRedisService redisService,
            IMessageQueueService messageQueueService,
            string nodeId,
            string nodeAddress,
            int nodePort,
            string endpoint = "/ws",
            int maxConnections = 0,
            LoadBalancingStrategy loadBalancingStrategy = LoadBalancingStrategy.LeastConnections,
            Func<Task<NodeInfo>> nodeInfoProvider = null)
        {
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));
            if (loggerFactory == null)
                throw new ArgumentNullException(nameof(loggerFactory));
            if (redisService == null)
                throw new ArgumentNullException(nameof(redisService));
            if (messageQueueService == null)
                throw new ArgumentNullException(nameof(messageQueueService));
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));
            if (string.IsNullOrEmpty(nodeAddress))
                throw new ArgumentNullException(nameof(nodeAddress));

            var nodeInfo = new NodeInfo
            {
                NodeId = nodeId,
                Address = nodeAddress,
                Port = nodePort,
                Endpoint = endpoint,
                MaxConnections = maxConnections,
                Status = NodeStatus.Active,
                RegisteredAt = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow
            };

            return new HybridClusterTransport(
                logger,
                loggerFactory,
                redisService,
                messageQueueService,
                nodeId,
                nodeInfo,
                loadBalancingStrategy,
                nodeInfoProvider);
        }
    }
}