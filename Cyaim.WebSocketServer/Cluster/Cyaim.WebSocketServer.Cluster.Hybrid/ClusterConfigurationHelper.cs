using System;
using Microsoft.Extensions.Configuration;

namespace Cyaim.WebSocketServer.Cluster.Hybrid
{
    /// <summary>
    /// Helper class for cluster configuration retrieval
    /// 集群配置获取辅助类
    /// Supports multiple configuration sources with priority: config file -> environment variables -> defaults
    /// 支持多种配置来源，优先级：配置文件 -> 环境变量 -> 默认值
    /// </summary>
    public static class ClusterConfigurationHelper
    {
        /// <summary>
        /// Cluster configuration result / 集群配置结果
        /// </summary>
        public class ClusterConfig
        {
            /// <summary>
            /// Node ID / 节点 ID
            /// </summary>
            public string NodeId { get; set; }

            /// <summary>
            /// Node address / 节点地址
            /// </summary>
            public string NodeAddress { get; set; }

            /// <summary>
            /// Node port / 节点端口
            /// </summary>
            public int NodePort { get; set; }

            /// <summary>
            /// WebSocket endpoint path / WebSocket 端点路径
            /// </summary>
            public string Endpoint { get; set; }

            /// <summary>
            /// Maximum connections / 最大连接数
            /// </summary>
            public int MaxConnections { get; set; }

            /// <summary>
            /// Load balancing strategy / 负载均衡策略
            /// </summary>
            public LoadBalancingStrategy LoadBalancingStrategy { get; set; }
        }

        /// <summary>
        /// Get cluster configuration from multiple sources with priority
        /// 从多个来源获取集群配置（带优先级）
        /// Priority: config file -> environment variables -> defaults
        /// 优先级：配置文件 -> 环境变量 -> 默认值
        /// </summary>
        /// <param name="configuration">Configuration instance / 配置实例</param>
        /// <param name="clusterSectionName">Cluster configuration section name, default is "Cluster" / 集群配置节名称，默认为 "Cluster"</param>
        /// <returns>Cluster configuration / 集群配置</returns>
        public static ClusterConfig GetClusterConfig(IConfiguration configuration, string clusterSectionName = "Cluster")
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            var clusterConfig = configuration.GetSection(clusterSectionName);

            // 节点ID：配置文件 -> 环境变量 -> 机器名
            // Node ID: config file -> environment variable -> machine name
            var nodeId = clusterConfig["NodeId"]
                ?? Environment.GetEnvironmentVariable("NODE_ID")
                ?? Environment.MachineName;

            // 节点地址：配置文件 -> 环境变量 -> localhost
            // Node address: config file -> environment variable -> localhost
            var nodeAddress = clusterConfig["NodeAddress"]
                ?? Environment.GetEnvironmentVariable("NODE_ADDRESS")
                ?? "localhost";

            // 节点端口：配置文件 -> 环境变量 -> Kestrel配置 -> 默认5000
            // Node port: config file -> environment variable -> Kestrel config -> default 5000
            int nodePort;
            if (clusterConfig.GetValue<int?>("NodePort") is { } configPort && configPort > 0)
            {
                nodePort = configPort;
            }
            else if (Environment.GetEnvironmentVariable("NODE_PORT") != null 
                && int.TryParse(Environment.GetEnvironmentVariable("NODE_PORT"), out var envPort) 
                && envPort > 0)
            {
                nodePort = envPort;
            }
            else if (configuration.GetSection("Kestrel:Endpoints:Http:Url").Value != null
                && Uri.TryCreate(configuration.GetSection("Kestrel:Endpoints:Http:Url").Value, UriKind.Absolute, out var kestrelUri))
            {
                nodePort = kestrelUri.Port;
            }
            else
            {
                nodePort = 5000;  // 默认端口 / Default port
            }

            // 端点路径：配置文件 -> 默认 /im
            // Endpoint path: config file -> default /im
            var endpoint = clusterConfig["Endpoint"] ?? "/im";

            // 最大连接数：配置文件 -> 默认 10000
            // Max connections: config file -> default 10000
            var maxConnections = clusterConfig.GetValue<int?>("MaxConnections") ?? 10000;

            // 负载均衡策略：配置文件 -> 默认 LeastConnections
            // Load balancing strategy: config file -> default LeastConnections
            var loadBalancingStrategyStr = clusterConfig["LoadBalancingStrategy"] ?? "LeastConnections";
            var loadBalancingStrategy = Enum.TryParse<LoadBalancingStrategy>(loadBalancingStrategyStr, out var strategy)
                ? strategy
                : LoadBalancingStrategy.LeastConnections;

            return new ClusterConfig
            {
                NodeId = nodeId,
                NodeAddress = nodeAddress,
                NodePort = nodePort,
                Endpoint = endpoint,
                MaxConnections = maxConnections,
                LoadBalancingStrategy = loadBalancingStrategy
            };
        }
    }
}

