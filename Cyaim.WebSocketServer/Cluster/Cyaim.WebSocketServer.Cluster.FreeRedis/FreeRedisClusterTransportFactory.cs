using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Cluster.FreeRedis
{
    /// <summary>
    /// Factory for creating FreeRedis cluster transport
    /// 创建 FreeRedis 集群传输的工厂
    /// </summary>
    public class FreeRedisClusterTransportFactory
    {
        /// <summary>
        /// Create FreeRedis cluster transport / 创建 FreeRedis 集群传输
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <param name="connectionString">Redis connection string / Redis 连接字符串</param>
        /// <returns>FreeRedis cluster transport instance / FreeRedis 集群传输实例</returns>
        public static IClusterTransport Create(
            ILogger<FreeRedisClusterTransport> logger,
            string nodeId,
            string connectionString)
        {
            return new FreeRedisClusterTransport(logger, nodeId, connectionString);
        }
    }
}

