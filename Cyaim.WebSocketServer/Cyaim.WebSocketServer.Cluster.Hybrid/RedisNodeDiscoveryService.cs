using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Cluster.Hybrid
{
    /// <summary>
    /// Redis-based node discovery service
    /// 基于 Redis 的节点发现服务
    /// </summary>
    public class RedisNodeDiscoveryService : IDisposable
    {
        private readonly ILogger<RedisNodeDiscoveryService> _logger;
        private readonly IRedisService _redisService;
        private readonly string _nodeId;
        private readonly NodeInfo _nodeInfo;
        private readonly string _clusterPrefix;
        private readonly Timer _heartbeatTimer;
        private readonly Timer _discoveryTimer;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _disposed = false;

        /// <summary>
        /// Event triggered when node discovered / 发现节点时触发的事件
        /// </summary>
        public event EventHandler<NodeInfo> NodeDiscovered;

        /// <summary>
        /// Event triggered when node removed / 节点移除时触发的事件
        /// </summary>
        public event EventHandler<string> NodeRemoved;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="redisService">Redis service / Redis 服务</param>
        /// <param name="nodeId">Current node ID / 当前节点 ID</param>
        /// <param name="nodeInfo">Current node information / 当前节点信息</param>
        /// <param name="clusterPrefix">Cluster prefix for Redis keys / Redis 键的集群前缀</param>
        public RedisNodeDiscoveryService(
            ILogger<RedisNodeDiscoveryService> logger,
            IRedisService redisService,
            string nodeId,
            NodeInfo nodeInfo,
            string clusterPrefix = "websocket:cluster")
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _redisService = redisService ?? throw new ArgumentNullException(nameof(redisService));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _nodeInfo = nodeInfo ?? throw new ArgumentNullException(nameof(nodeInfo));
            _clusterPrefix = clusterPrefix;
            _cancellationTokenSource = new CancellationTokenSource();

            // Heartbeat every 5 seconds / 每 5 秒发送一次心跳
            _heartbeatTimer = new Timer(SendHeartbeat, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            
            // Discover nodes every 10 seconds / 每 10 秒发现一次节点
            _discoveryTimer = new Timer(DiscoverNodes, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// Start the discovery service / 启动发现服务
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogInformation($"Starting Redis node discovery service for node {_nodeId}");

            await _redisService.ConnectAsync();

            // Register current node / 注册当前节点
            await RegisterNodeAsync();

            // Subscribe to node changes / 订阅节点变更
            await _redisService.SubscribeAsync($"{_clusterPrefix}:events", async (channel, message) =>
            {
                try
                {
                    var eventData = JsonSerializer.Deserialize<NodeEvent>(message);
                    if (eventData != null)
                    {
                        if (eventData.EventType == "node_joined" || eventData.EventType == "node_updated")
                        {
                            var nodeInfo = JsonSerializer.Deserialize<NodeInfo>(eventData.NodeData);
                            if (nodeInfo != null && nodeInfo.NodeId != _nodeId)
                            {
                                NodeDiscovered?.Invoke(this, nodeInfo);
                            }
                        }
                        else if (eventData.EventType == "node_left")
                        {
                            NodeRemoved?.Invoke(this, eventData.NodeId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing node event");
                }
            });

            // Initial discovery / 初始发现
            await DiscoverNodesAsync();
        }

        /// <summary>
        /// Stop the discovery service / 停止发现服务
        /// </summary>
        public async Task StopAsync()
        {
            _logger.LogInformation($"Stopping Redis node discovery service for node {_nodeId}");

            _cancellationTokenSource.Cancel();
            _heartbeatTimer?.Dispose();
            _discoveryTimer?.Dispose();

            // Unregister current node / 注销当前节点
            await UnregisterNodeAsync();

            await _redisService.DisconnectAsync();
        }

        /// <summary>
        /// Register current node / 注册当前节点
        /// </summary>
        private async Task RegisterNodeAsync()
        {
            try
            {
                var key = $"{_clusterPrefix}:nodes:{_nodeId}";
                var value = JsonSerializer.Serialize(_nodeInfo);
                
                // Set node info with 30 seconds expiration, will be refreshed by heartbeat / 设置节点信息，30 秒过期，由心跳刷新
                await _redisService.SetAsync(key, value, TimeSpan.FromSeconds(30));

                // Publish node join event / 发布节点加入事件
                var nodeEvent = new NodeEvent
                {
                    EventType = "node_joined",
                    NodeId = _nodeId,
                    NodeData = value,
                    Timestamp = DateTime.UtcNow
                };
                await _redisService.PublishAsync($"{_clusterPrefix}:events", JsonSerializer.Serialize(nodeEvent));

                _logger.LogInformation($"Node {_nodeId} registered in Redis");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to register node {_nodeId}");
            }
        }

        /// <summary>
        /// Unregister current node / 注销当前节点
        /// </summary>
        private async Task UnregisterNodeAsync()
        {
            try
            {
                var key = $"{_clusterPrefix}:nodes:{_nodeId}";
                await _redisService.DeleteAsync(key);

                // Publish node leave event / 发布节点离开事件
                var nodeEvent = new NodeEvent
                {
                    EventType = "node_left",
                    NodeId = _nodeId,
                    Timestamp = DateTime.UtcNow
                };
                await _redisService.PublishAsync($"{_clusterPrefix}:events", JsonSerializer.Serialize(nodeEvent));

                _logger.LogInformation($"Node {_nodeId} unregistered from Redis");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to unregister node {_nodeId}");
            }
        }

        /// <summary>
        /// Send heartbeat / 发送心跳
        /// </summary>
        private async void SendHeartbeat(object state)
        {
            if (_disposed || _cancellationTokenSource.Token.IsCancellationRequested)
                return;

            try
            {
                _nodeInfo.LastHeartbeat = DateTime.UtcNow;
                var key = $"{_clusterPrefix}:nodes:{_nodeId}";
                var value = JsonSerializer.Serialize(_nodeInfo);
                
                // Refresh node info with 30 seconds expiration / 刷新节点信息，30 秒过期
                await _redisService.SetAsync(key, value, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send heartbeat for node {_nodeId}");
            }
        }

        /// <summary>
        /// Discover nodes / 发现节点
        /// </summary>
        private async void DiscoverNodes(object state)
        {
            if (_disposed || _cancellationTokenSource.Token.IsCancellationRequested)
                return;

            await DiscoverNodesAsync();
        }

        /// <summary>
        /// Discover nodes asynchronously / 异步发现节点
        /// </summary>
        private async Task DiscoverNodesAsync()
        {
            try
            {
                var pattern = $"{_clusterPrefix}:nodes:*";
                var nodes = await _redisService.GetValuesAsync(pattern);

                var discoveredNodeIds = new HashSet<string>();

                foreach (var kvp in nodes)
                {
                    try
                    {
                        var nodeInfo = JsonSerializer.Deserialize<NodeInfo>(kvp.Value);
                        if (nodeInfo != null && nodeInfo.NodeId != _nodeId)
                        {
                            discoveredNodeIds.Add(nodeInfo.NodeId);

                            // Check if node is still alive (heartbeat within 60 seconds) / 检查节点是否仍然存活（60 秒内有心跳）
                            if (DateTime.UtcNow - nodeInfo.LastHeartbeat < TimeSpan.FromSeconds(60))
                            {
                                NodeDiscovered?.Invoke(this, nodeInfo);
                            }
                            else
                            {
                                // Node appears to be dead, remove it / 节点似乎已死，移除它
                                _logger.LogWarning($"Node {nodeInfo.NodeId} appears to be dead, removing from discovery");
                                await _redisService.DeleteAsync(kvp.Key);
                                NodeRemoved?.Invoke(this, nodeInfo.NodeId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to deserialize node info from key {kvp.Key}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to discover nodes");
            }
        }

        /// <summary>
        /// Get all discovered nodes / 获取所有发现的节点
        /// </summary>
        public async Task<List<NodeInfo>> GetDiscoveredNodesAsync()
        {
            try
            {
                var pattern = $"{_clusterPrefix}:nodes:*";
                var nodes = await _redisService.GetValuesAsync(pattern);

                var nodeList = new List<NodeInfo>();

                foreach (var kvp in nodes)
                {
                    try
                    {
                        var nodeInfo = JsonSerializer.Deserialize<NodeInfo>(kvp.Value);
                        if (nodeInfo != null && nodeInfo.NodeId != _nodeId)
                        {
                            // Only include alive nodes / 只包含存活的节点
                            if (DateTime.UtcNow - nodeInfo.LastHeartbeat < TimeSpan.FromSeconds(60))
                            {
                                nodeList.Add(nodeInfo);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to deserialize node info from key {kvp.Key}");
                    }
                }

                return nodeList;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get discovered nodes");
                return new List<NodeInfo>();
            }
        }

        /// <summary>
        /// Update node information / 更新节点信息
        /// </summary>
        public async Task UpdateNodeInfoAsync(NodeInfo nodeInfo)
        {
            try
            {
                var key = $"{_clusterPrefix}:nodes:{_nodeId}";
                var value = JsonSerializer.Serialize(nodeInfo);
                
                await _redisService.SetAsync(key, value, TimeSpan.FromSeconds(30));

                // Publish node update event / 发布节点更新事件
                var nodeEvent = new NodeEvent
                {
                    EventType = "node_updated",
                    NodeId = _nodeId,
                    NodeData = value,
                    Timestamp = DateTime.UtcNow
                };
                await _redisService.PublishAsync($"{_clusterPrefix}:events", JsonSerializer.Serialize(nodeEvent));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update node info for node {_nodeId}");
            }
        }

        /// <summary>
        /// Dispose / 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cancellationTokenSource.Cancel();
            _heartbeatTimer?.Dispose();
            _discoveryTimer?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        /// <summary>
        /// Node event / 节点事件
        /// </summary>
        private class NodeEvent
        {
            public string EventType { get; set; }
            public string NodeId { get; set; }
            public string NodeData { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}

