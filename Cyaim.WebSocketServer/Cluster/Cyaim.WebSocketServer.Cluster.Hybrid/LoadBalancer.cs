using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Cluster.Hybrid
{
    /// <summary>
    /// Load balancer for selecting optimal node
    /// 用于选择最优节点的负载均衡器
    /// </summary>
    public class LoadBalancer
    {
        private readonly ILogger<LoadBalancer> _logger;
        private readonly LoadBalancingStrategy _strategy;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="strategy">Load balancing strategy / 负载均衡策略</param>
        public LoadBalancer(ILogger<LoadBalancer> logger, LoadBalancingStrategy strategy = LoadBalancingStrategy.LeastConnections)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _strategy = strategy;
        }

        /// <summary>
        /// Select optimal node from available nodes / 从可用节点中选择最优节点
        /// </summary>
        /// <param name="nodes">Available nodes / 可用节点</param>
        /// <param name="excludeNodeId">Node ID to exclude / 要排除的节点 ID</param>
        /// <returns>Selected node info or null if no suitable node / 选中的节点信息，如果没有合适的节点则返回 null</returns>
        public NodeInfo SelectNode(List<NodeInfo> nodes, string excludeNodeId = null)
        {
            if (nodes == null || nodes.Count == 0)
            {
                _logger.LogWarning("No nodes available for load balancing");
                return null;
            }

            // Filter out excluded node and offline/draining nodes / 过滤掉排除的节点和离线/排空节点
            var availableNodes = nodes
                .Where(n => n.NodeId != excludeNodeId)
                .Where(n => n.Status == NodeStatus.Active)
                .Where(n => n.ConnectionCount < n.MaxConnections || n.MaxConnections == 0)
                .ToList();

            if (availableNodes.Count == 0)
            {
                _logger.LogWarning("No available nodes after filtering");
                return null;
            }

            NodeInfo selectedNode = null;

            switch (_strategy)
            {
                case LoadBalancingStrategy.LeastConnections:
                    selectedNode = availableNodes
                        .OrderBy(n => n.ConnectionCount)
                        .ThenBy(n => n.CpuUsage)
                        .ThenBy(n => n.MemoryUsage)
                        .FirstOrDefault();
                    break;

                case LoadBalancingStrategy.RoundRobin:
                    selectedNode = availableNodes
                        .OrderBy(n => n.LastHeartbeat)
                        .FirstOrDefault();
                    break;

                case LoadBalancingStrategy.LeastResourceUsage:
                    selectedNode = availableNodes
                        .OrderBy(n => (n.CpuUsage + n.MemoryUsage) / 2.0)
                        .ThenBy(n => n.ConnectionCount)
                        .FirstOrDefault();
                    break;

                case LoadBalancingStrategy.Random:
                    var random = new Random();
                    selectedNode = availableNodes[random.Next(availableNodes.Count)];
                    break;

                default:
                    selectedNode = availableNodes.FirstOrDefault();
                    break;
            }

            if (selectedNode != null)
            {
                _logger.LogDebug($"Selected node {selectedNode.NodeId} using {_strategy} strategy (connections: {selectedNode.ConnectionCount}, CPU: {selectedNode.CpuUsage}%, Memory: {selectedNode.MemoryUsage}%)");
            }

            return selectedNode;
        }

        /// <summary>
        /// Calculate node score for comparison / 计算节点分数用于比较
        /// </summary>
        /// <param name="node">Node info / 节点信息</param>
        /// <returns>Node score (lower is better) / 节点分数（越低越好）</returns>
        public double CalculateNodeScore(NodeInfo node)
        {
            if (node == null)
                return double.MaxValue;

            // Normalize connection count (0-1) / 标准化连接数（0-1）
            var connectionRatio = node.MaxConnections > 0 
                ? (double)node.ConnectionCount / node.MaxConnections 
                : 0.0;

            // Weighted score: connections (50%), CPU (25%), Memory (25%) / 加权分数：连接数（50%），CPU（25%），内存（25%）
            var score = connectionRatio * 0.5 + (node.CpuUsage / 100.0) * 0.25 + (node.MemoryUsage / 100.0) * 0.25;

            return score;
        }
    }

    /// <summary>
    /// Load balancing strategy / 负载均衡策略
    /// </summary>
    public enum LoadBalancingStrategy
    {
        /// <summary>
        /// Least connections - select node with fewest connections / 最少连接 - 选择连接数最少的节点
        /// </summary>
        LeastConnections,

        /// <summary>
        /// Round robin - select nodes in rotation / 轮询 - 轮换选择节点
        /// </summary>
        RoundRobin,

        /// <summary>
        /// Least resource usage - select node with lowest CPU/memory usage / 最少资源使用 - 选择 CPU/内存使用率最低的节点
        /// </summary>
        LeastResourceUsage,

        /// <summary>
        /// Random - randomly select a node / 随机 - 随机选择一个节点
        /// </summary>
        Random
    }
}

