using Cyaim.WebSocketServer.Infrastructure.Configures;
using System;
using System.Collections.Generic;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Global cluster status center
    /// 全局集群状态中心
    /// </summary>
    public static class GlobalClusterCenter
    {
        /// <summary>
        /// Cluster context / 集群上下文
        /// </summary>
        public static ClusterOption ClusterContext { get; set; }

        /// <summary>
        /// Cluster manager instance / 集群管理器实例
        /// </summary>
        public static ClusterManager ClusterManager { get; set; }

        /// <summary>
        /// WebSocket connection provider instance / WebSocket 连接提供者实例
        /// </summary>
        public static IWebSocketConnectionProvider ConnectionProvider { get; set; }
    }
}