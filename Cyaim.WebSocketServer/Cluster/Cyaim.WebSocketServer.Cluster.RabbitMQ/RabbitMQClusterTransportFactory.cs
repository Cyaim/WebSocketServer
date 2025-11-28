using System;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Cluster.RabbitMQ
{
    /// <summary>
    /// Factory for creating RabbitMQ cluster transport instances
    /// 创建 RabbitMQ 集群传输实例的工厂类
    /// </summary>
    public static class RabbitMQClusterTransportFactory
    {
        /// <summary>
        /// Create RabbitMQ cluster transport
        /// 创建 RabbitMQ 集群传输实例
        /// </summary>
        /// <param name="loggerFactory">Logger factory / 日志工厂</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <param name="clusterOption">Cluster configuration / 集群配置</param>
        /// <returns>RabbitMQ cluster transport instance / RabbitMQ 集群传输实例</returns>
        /// <exception cref="ArgumentNullException">When clusterOption is null / 当 clusterOption 为 null 时</exception>
        /// <exception cref="ArgumentException">When RabbitMQ connection string is missing / 当缺少 RabbitMQ 连接字符串时</exception>
        public static IClusterTransport CreateTransport(
            ILoggerFactory loggerFactory,
            string nodeId,
            ClusterOption clusterOption)
        {
            if (clusterOption == null)
                throw new ArgumentNullException(nameof(clusterOption));

            if (string.IsNullOrEmpty(clusterOption.RabbitMQConnectionString))
            {
                throw new ArgumentException("RabbitMQ connection string is required for RabbitMQ transport", nameof(clusterOption));
            }

            return new RabbitMQClusterTransport(
                loggerFactory.CreateLogger<RabbitMQClusterTransport>(),
                nodeId,
                clusterOption.RabbitMQConnectionString);
        }
    }
}

