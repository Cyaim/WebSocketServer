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

                // Get Raft node info for current node / 获取当前节点的 Raft 信息
                var raftNode = GetRaftNodeFromManager(clusterManager);
                var currentTerm = raftNode?.CurrentTerm ?? 0;
                var isLeader = clusterManager.IsLeader();
                var leaderId = raftNode?.LeaderId;

                // Get current node info / 获取当前节点信息
                var currentNode = new NodeStatusInfo
                {
                    NodeId = clusterContext.NodeId,
                    Address = clusterContext.NodeAddress,
                    Port = clusterContext.NodePort,
                    State = "Connected",
                    IsLeader = isLeader,
                    IsConnected = true,
                    ConnectionCount = clusterManager.GetLocalConnectionCount(),
                    CurrentTerm = currentTerm,
                    LeaderId = leaderId,
                    LogLength = raftNode?.Log?.Count ?? 0
                };

                nodes.Add(currentNode);

                // Add other nodes from cluster context / 从集群上下文添加其他节点
                if (clusterContext.Nodes != null)
                {
                    foreach (var nodeConfig in clusterContext.Nodes)
                    {
                        if (string.IsNullOrEmpty(nodeConfig))
                            continue;

                        try
                        {
                            string nodeId = null;
                            string address = null;
                            int port = 0;
                            bool isConnected = false;

                            // Try to parse as URI first (format: ws://address:port/nodeId) / 首先尝试解析为 URI（格式：ws://address:port/nodeId）
                            if (Uri.TryCreate(nodeConfig, UriKind.Absolute, out var uri))
                            {
                                address = uri.Host;
                                port = uri.Port > 0 ? uri.Port : (uri.Scheme == "ws" || uri.Scheme == "wss" ? 80 : 0);

                                // Extract node ID from path / 从路径提取节点 ID
                                var path = uri.PathAndQuery.TrimStart('/');
                                nodeId = !string.IsNullOrEmpty(path) ? path : $"{uri.Host}:{uri.Port}";
                            }
                            else
                            {
                                // Try format: nodeId@address:port or nodeId@address / 尝试格式：nodeId@address:port 或 nodeId@address
                                var parts = nodeConfig.Split('@');
                                if (parts.Length == 2)
                                {
                                    nodeId = parts[0];
                                    var addressParts = parts[1].Split(':');
                                    address = addressParts[0];
                                    port = addressParts.Length > 1 && int.TryParse(addressParts[1], out var p) ? p : 0;
                                }
                                else
                                {
                                    // Assume it's just a node ID / 假设它只是一个节点 ID
                                    nodeId = nodeConfig;
                                }
                            }

                            if (!string.IsNullOrEmpty(nodeId) && nodeId != currentNode.NodeId)
                            {
                                // Check if node is connected via cluster manager / 通过集群管理器检查节点是否已连接
                                if (clusterManager != null)
                                {
                                    // Try to get connection status from transport / 尝试从传输层获取连接状态
                                    var transport = GetTransportFromManager(clusterManager);
                                    if (transport is Infrastructure.Cluster.Transports.WebSocketClusterTransport wsTransport)
                                    {
                                        isConnected = wsTransport.IsNodeConnected(nodeId);
                                    }
                                }

                                // Get current Raft info to determine if this node is the leader / 获取当前 Raft 信息以确定此节点是否为领导者
                                var currentRaftNode = GetRaftNodeFromManager(clusterManager);
                                string currLeaderId = currentRaftNode?.LeaderId;
                                bool isRemoteLeader = nodeId == currLeaderId;

                                nodes.Add(new NodeStatusInfo
                                {
                                    NodeId = nodeId,
                                    Address = address ?? "unknown",
                                    Port = port,
                                    State = isConnected ? "Connected" : "Disconnected",
                                    IsConnected = isConnected,
                                    ConnectionCount = 0, // TODO: Get from cluster manager / 从集群管理器获取
                                    CurrentTerm = currentRaftNode?.CurrentTerm ?? 0, // Use current node's term as reference / 使用当前节点的任期作为参考
                                    IsLeader = isRemoteLeader,
                                    LeaderId = currLeaderId,
                                    LogLength = 0
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log and continue with next node / 记录错误并继续处理下一个节点
                            System.Diagnostics.Debug.WriteLine($"Failed to parse node config '{nodeConfig}': {ex.Message}");
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

        /// <summary>
        /// Get transport instance from cluster manager using reflection
        /// 使用反射从集群管理器获取传输实例
        /// </summary>
        private static IClusterTransport GetTransportFromManager(ClusterManager manager)
        {
            if (manager == null)
                return null;

            var transportField = typeof(ClusterManager)
                .GetField("_transport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return transportField?.GetValue(manager) as IClusterTransport;
        }

        /// <summary>
        /// Get Raft node instance from cluster manager using reflection
        /// 使用反射从集群管理器获取 Raft 节点实例
        /// </summary>
        private static RaftNode GetRaftNodeFromManager(ClusterManager manager)
        {
            if (manager == null)
                return null;

            var raftNodeField = typeof(ClusterManager)
                .GetField("_raftNode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return raftNodeField?.GetValue(manager) as RaftNode;
        }
    }
}

