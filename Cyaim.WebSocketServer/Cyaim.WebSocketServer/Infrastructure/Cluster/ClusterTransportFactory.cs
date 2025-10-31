using System;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Cluster.Transports;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Factory for creating cluster transport instances
    /// 创建集群传输实例的工厂类
    /// Note: Only WebSocket transport is supported in the base package.
    /// 注意：基础包中仅支持 WebSocket 传输。
    /// For Redis and RabbitMQ transports, use the separate extension packages:
    /// 对于 Redis 和 RabbitMQ 传输，请使用独立的扩展包：
    /// - Cyaim.WebSocketServer.Cluster.StackExchangeRedis
    /// - Cyaim.WebSocketServer.Cluster.RabbitMQ
    /// </summary>
    public static class ClusterTransportFactory
    {
        /// <summary>
        /// Create cluster transport based on configuration
        /// 根据配置创建集群传输实例
        /// </summary>
        /// <param name="loggerFactory">Logger factory / 日志工厂</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <param name="clusterOption">Cluster configuration / 集群配置</param>
        /// <returns>Cluster transport instance / 集群传输实例</returns>
        /// <exception cref="ArgumentNullException">When clusterOption is null / 当 clusterOption 为 null 时</exception>
        /// <exception cref="NotSupportedException">When transport type is not supported / 当传输类型不支持时</exception>
        public static IClusterTransport CreateTransport(
            ILoggerFactory loggerFactory,
            string nodeId,
            ClusterOption clusterOption)
        {
            if (clusterOption == null)
                throw new ArgumentNullException(nameof(clusterOption));

            var transportType = (clusterOption.TransportType ?? "ws").ToLower();

            switch (transportType)
            {
                case "ws":
                case "wss":
                case "websocket":
                    return new WebSocketClusterTransport(
                        loggerFactory.CreateLogger<WebSocketClusterTransport>(),
                        nodeId);

                case "redis":
                    throw new NotSupportedException(
                        "Redis transport is not available in the base package. " +
                        "Please install Cyaim.WebSocketServer.Cluster.StackExchangeRedis package and use RedisClusterTransportFactory. " +
                        "Redis 传输在基础包中不可用。请安装 Cyaim.WebSocketServer.Cluster.StackExchangeRedis 包并使用 RedisClusterTransportFactory。");

                case "rabbitmq":
                    throw new NotSupportedException(
                        "RabbitMQ transport is not available in the base package. " +
                        "Please install Cyaim.WebSocketServer.Cluster.RabbitMQ package and use RabbitMQClusterTransportFactory. " +
                        "RabbitMQ 传输在基础包中不可用。请安装 Cyaim.WebSocketServer.Cluster.RabbitMQ 包并使用 RabbitMQClusterTransportFactory。");

                default:
                    throw new NotSupportedException(
                        $"Transport type '{transportType}' is not supported. " +
                        "Supported types in base package: ws. " +
                        "For Redis and RabbitMQ, use the extension packages. " +
                        $"不支持的传输类型 '{transportType}'。基础包中支持的类型：ws。对于 Redis 和 RabbitMQ，请使用扩展包。");
            }
        }
    }
}

