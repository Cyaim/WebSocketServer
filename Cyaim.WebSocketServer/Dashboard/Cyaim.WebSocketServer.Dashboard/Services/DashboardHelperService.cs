using System;
using System.Collections.Generic;
using System.Linq;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Dashboard.Models;

namespace Cyaim.WebSocketServer.Dashboard.Services
{
    /// <summary>
    /// Dashboard helper service for shared functionality
    /// Dashboard 辅助服务，用于共享功能
    /// </summary>
    public class DashboardHelperService
    {
        /// <summary>
        /// Get all connections from cluster routing table / 从集群路由表获取所有连接
        /// </summary>
        /// <returns>Dictionary of connection ID to node ID / 连接ID到节点ID的字典</returns>
        public Dictionary<string, string> GetAllClusterConnections()
        {
            var connections = new Dictionary<string, string>();
            var clusterManager = GlobalClusterCenter.ClusterManager;
            var currentNodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown";

            // Get local connections / 获取本地连接
            if (MvcChannelHandler.Clients != null)
            {
                foreach (var kvp in MvcChannelHandler.Clients)
                {
                    connections[kvp.Key] = currentNodeId;
                }
            }

            // Get connections from cluster routing table / 从集群路由表获取连接
            if (clusterManager != null && clusterManager.ConnectionRoutes != null)
            {
                foreach (var kvp in clusterManager.ConnectionRoutes)
                {
                    // Merge with local connections, cluster routes take precedence / 与本地连接合并，集群路由优先
                    connections[kvp.Key] = kvp.Value;
                }
            }

            return connections;
        }

        /// <summary>
        /// Get node status list / 获取节点状态列表
        /// </summary>
        /// <returns>Node status list / 节点状态列表</returns>
        public List<NodeStatusInfo> GetNodeStatusList()
        {
            var nodes = new List<NodeStatusInfo>();

            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                var clusterContext = GlobalClusterCenter.ClusterContext;

                if (clusterManager == null || clusterContext == null)
                {
                    // Return current node only / 仅返回当前节点
                    return new List<NodeStatusInfo>
                    {
                        new NodeStatusInfo
                        {
                            NodeId = "unknown",
                            State = "Unknown",
                            IsConnected = false,
                            ConnectionCount = MvcChannelHandler.Clients?.Count ?? 0
                        }
                    };
                }

                // Get current node info / 获取当前节点信息
                var currentNode = new NodeStatusInfo
                {
                    NodeId = clusterContext.NodeId,
                    Address = clusterContext.NodeAddress,
                    Port = clusterContext.NodePort,
                    State = "Unknown",
                    IsLeader = clusterManager.IsLeader(),
                    IsConnected = true,
                    ConnectionCount = clusterManager.GetLocalConnectionCount(),
                    CurrentTerm = 0,
                    LogLength = 0
                };

                nodes.Add(currentNode);

                // Add other nodes from cluster context / 从集群上下文添加其他节点
                if (clusterContext.Nodes != null)
                {
                    foreach (var nodeConfig in clusterContext.Nodes)
                    {
                        if (string.IsNullOrEmpty(nodeConfig))
                            continue;

                        // Parse node config / 解析节点配置
                        var parts = nodeConfig.Split('@');
                        if (parts.Length >= 2)
                        {
                            var nodeId = parts[0];
                            var addressParts = parts[1].Split(':');
                            var address = addressParts[0];
                            var port = addressParts.Length > 1 && int.TryParse(addressParts[1], out var p) ? p : 0;

                            if (nodeId != currentNode.NodeId)
                            {
                                nodes.Add(new NodeStatusInfo
                                {
                                    NodeId = nodeId,
                                    Address = address,
                                    Port = port,
                                    State = "Unknown",
                                    IsConnected = false,
                                    ConnectionCount = 0
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Error handling is done by caller / 错误处理由调用者完成
            }

            return nodes;
        }
    }
}

