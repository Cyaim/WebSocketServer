using System;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Cluster.StackExchangeRedis
{
    /// <summary>
    /// Factory for creating Redis cluster transport instances
    /// 创建 Redis 集群传输实例的工厂类
    /// </summary>
    public static class RedisClusterTransportFactory
    {
        /// <summary>
        /// Create Redis cluster transport
        /// 创建 Redis 集群传输实例
        /// </summary>
        /// <param name="loggerFactory">Logger factory / 日志工厂</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <param name="clusterOption">Cluster configuration / 集群配置</param>
        /// <returns>Redis cluster transport instance / Redis 集群传输实例</returns>
        /// <exception cref="ArgumentNullException">When clusterOption is null / 当 clusterOption 为 null 时</exception>
        /// <exception cref="ArgumentException">When Redis connection string is missing / 当缺少 Redis 连接字符串时</exception>
        public static IClusterTransport CreateTransport(
            ILoggerFactory loggerFactory,
            string nodeId,
            ClusterOption clusterOption)
        {
            if (clusterOption == null)
                throw new ArgumentNullException(nameof(clusterOption));

            if (string.IsNullOrEmpty(clusterOption.RedisConnectionString))
            {
                throw new ArgumentException("Redis connection string is required for Redis transport", nameof(clusterOption));
            }

            return new RedisClusterTransport(
                loggerFactory.CreateLogger<RedisClusterTransport>(),
                nodeId,
                clusterOption.RedisConnectionString);
        }
    }
}

