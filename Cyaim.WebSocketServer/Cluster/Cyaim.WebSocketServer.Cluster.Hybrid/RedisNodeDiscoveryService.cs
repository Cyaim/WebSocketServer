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
        private NodeInfo _nodeInfo;
        private readonly string _clusterPrefix;

        /// <summary>
        /// Single Redis hash key holding every node's registration (field = nodeId, value = NodeInfo JSON).
        /// One key ⇒ one cluster shard ⇒ HGETALL reliably returns all nodes even on Redis Cluster.
        /// 存放所有节点注册的单个 Redis Hash 键（field=节点ID，value=NodeInfo JSON）。
        /// 单键=单分片，即使在 Redis 集群下 HGETALL 也能可靠返回全部节点。
        /// </summary>
        private string NodesHashKey => $"{_clusterPrefix}:nodes";
        private readonly Timer _heartbeatTimer;
        private readonly Timer _discoveryTimer;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Func<Task<NodeInfo>> _nodeInfoProvider;
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
        /// <param name="nodeInfoProvider">Optional function to get latest node info for heartbeat / 可选函数，用于在心跳时获取最新节点信息</param>
        public RedisNodeDiscoveryService(
            ILogger<RedisNodeDiscoveryService> logger,
            IRedisService redisService,
            string nodeId,
            NodeInfo nodeInfo,
            string clusterPrefix = "websocket:cluster",
            Func<Task<NodeInfo>> nodeInfoProvider = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _redisService = redisService ?? throw new ArgumentNullException(nameof(redisService));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _nodeInfo = nodeInfo ?? throw new ArgumentNullException(nameof(nodeInfo));
            _clusterPrefix = clusterPrefix;
            _nodeInfoProvider = nodeInfoProvider;
            _cancellationTokenSource = new CancellationTokenSource();

            // 定时器先创建但不触发，等 StartAsync 里 Redis 连接就绪、节点已注册后再启用，
            // 避免在 ConnectAsync 之前触发导致 "Redis is not connected" 错误日志刷屏。
            // Timers are created disabled and enabled in StartAsync after Redis is connected and the
            // node is registered, avoiding "Redis is not connected" error spam before ConnectAsync.
            _heartbeatTimer = new Timer(SendHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
            _discoveryTimer = new Timer(DiscoverNodes, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Start the discovery service / 启动发现服务
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogWarning($"[RedisNodeDiscoveryService] 启动 Redis 节点发现服务 - NodeId: {_nodeId}, ClusterPrefix: {_clusterPrefix}");

            await _redisService.ConnectAsync();
            _logger.LogWarning($"[RedisNodeDiscoveryService] Redis 连接成功 - NodeId: {_nodeId}");

            // Register current node / 注册当前节点
            await RegisterNodeAsync();
            _logger.LogWarning($"[RedisNodeDiscoveryService] 节点注册完成 - NodeId: {_nodeId}");

            // Subscribe to node changes / 订阅节点变更
            await _redisService.SubscribeAsync($"{_clusterPrefix}:events", async (channel, message) =>
            {
                try
                {
                    _logger.LogWarning($"[RedisNodeDiscoveryService] 收到节点事件 - Channel: {channel}, Message: {message}");
                    var eventData = JsonSerializer.Deserialize<NodeEvent>(message);
                    if (eventData != null)
                    {
                        if (eventData.EventType == "node_joined" || eventData.EventType == "node_updated")
                        {
                            var nodeInfo = JsonSerializer.Deserialize<NodeInfo>(eventData.NodeData);
                            if (nodeInfo != null && nodeInfo.NodeId != _nodeId)
                            {
                                _logger.LogWarning($"[RedisNodeDiscoveryService] 触发节点发现事件 - NodeId: {nodeInfo.NodeId}, EventType: {eventData.EventType}");
                                NodeDiscovered?.Invoke(this, nodeInfo);
                            }
                        }
                        else if (eventData.EventType == "node_left")
                        {
                            _logger.LogWarning($"[RedisNodeDiscoveryService] 触发节点移除事件 - NodeId: {eventData.NodeId}");
                            NodeRemoved?.Invoke(this, eventData.NodeId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[RedisNodeDiscoveryService] 处理节点事件时发生错误 - Channel: {channel}, Message: {message}");
                }
            });
            _logger.LogWarning($"[RedisNodeDiscoveryService] 节点事件订阅完成 - NodeId: {_nodeId}, Channel: {_clusterPrefix}:events");

            // Initial discovery / 初始发现
            await DiscoverNodesAsync();
            _logger.LogWarning($"[RedisNodeDiscoveryService] 初始节点发现完成 - NodeId: _nodeId: {_nodeId}");

            // Redis 已连接且本节点已注册，现在启用周期性心跳与发现定时器
            // Redis is connected and this node is registered; now enable the periodic timers.
            _heartbeatTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            _discoveryTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
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
                // 集群安全：所有节点写入同一个 Hash 键的不同字段（单键=单分片），
                // 避免 KEYS/SCAN 在 Redis 集群下只扫描单个分片导致发现不到其他分片上的节点键。
                // Cluster-safe: every node writes a field into the SAME hash key (one key = one shard),
                // avoiding KEYS/SCAN missing node keys sharded onto other cluster nodes.
                var value = JsonSerializer.Serialize(_nodeInfo);
                await _redisService.HashSetAsync(NodesHashKey, _nodeId, value);

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
                await _redisService.HashDeleteAsync(NodesHashKey, _nodeId);

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
                // If node info provider is available, get latest info / 如果节点信息提供者可用，获取最新信息
                NodeInfo nodeInfoToUpdate;
                if (_nodeInfoProvider != null)
                {
                    try
                    {
                        nodeInfoToUpdate = await _nodeInfoProvider();
                        if (nodeInfoToUpdate != null)
                        {
                            // Preserve node ID and other essential fields / 保留节点 ID 和其他必需字段
                            nodeInfoToUpdate.NodeId = _nodeId;
                            nodeInfoToUpdate.LastHeartbeat = DateTime.UtcNow;
                            _nodeInfo = nodeInfoToUpdate;
                        }
                        else
                        {
                            // Fallback to current node info / 回退到当前节点信息
                            _nodeInfo.LastHeartbeat = DateTime.UtcNow;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to get latest node info from provider, using cached info");
                        _nodeInfo.LastHeartbeat = DateTime.UtcNow;
                    }
                }
                else
                {
                    // No provider, just update heartbeat / 没有提供者，只更新心跳
                    _nodeInfo.LastHeartbeat = DateTime.UtcNow;
                }

                // Refresh this node's field in the shared hash (see RegisterNodeAsync for why a hash)
                // 刷新共享 Hash 中本节点的字段（用 Hash 的原因见 RegisterNodeAsync）
                var value = JsonSerializer.Serialize(_nodeInfo);
                await _redisService.HashSetAsync(NodesHashKey, _nodeId, value);
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
        public async Task DiscoverNodesAsync()
        {
            try
            {
                // 集群安全：从单个共享 Hash 键读取全部成员，HGETALL 只命中一个分片，能可靠拿到所有节点。
                // Cluster-safe: read the whole membership from one shared hash key. HGETALL hits a single
                // shard and reliably returns every node, unlike KEYS/SCAN which misses cross-shard keys.
                var nodes = await _redisService.HashGetAllAsync(NodesHashKey);
                _logger.LogDebug($"[RedisNodeDiscoveryService] 从 Redis Hash 获取到节点数据 - Key: {NodesHashKey}, Fields: {nodes.Count}, CurrentNodeId: {_nodeId}");

                var discoveredNodeIds = new HashSet<string>();

                foreach (var kvp in nodes)
                {
                    try
                    {
                        var nodeInfo = JsonSerializer.Deserialize<NodeInfo>(kvp.Value);
                        if (nodeInfo != null)
                        {
                            if (nodeInfo.NodeId != _nodeId)
                            {
                                discoveredNodeIds.Add(nodeInfo.NodeId);

                                // Check if node is still alive (heartbeat within 60 seconds) / 检查节点是否仍然存活（60 秒内有心跳）
                                var timeSinceHeartbeat = DateTime.UtcNow - nodeInfo.LastHeartbeat;
                                if (timeSinceHeartbeat < TimeSpan.FromSeconds(60))
                                {
                                    NodeDiscovered?.Invoke(this, nodeInfo);
                                }
                                else
                                {
                                    // Node appears to be dead, remove its field / 节点似乎已死，删除其字段
                                    _logger.LogWarning($"[RedisNodeDiscoveryService] 节点已死亡，移除 - NodeId: {nodeInfo.NodeId}, TimeSinceHeartbeat: {timeSinceHeartbeat.TotalSeconds}秒");
                                    await _redisService.HashDeleteAsync(NodesHashKey, kvp.Key);
                                    NodeRemoved?.Invoke(this, nodeInfo.NodeId);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"[RedisNodeDiscoveryService] 节点信息解析为 null - Field: {kvp.Key}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"[RedisNodeDiscoveryService] 反序列化节点信息失败 - Field: {kvp.Key}, Error: {ex.Message}");
                    }
                }

                _logger.LogDebug($"[RedisNodeDiscoveryService] 节点发现完成 - Key: {NodesHashKey}, DiscoveredNodeCount: {discoveredNodeIds.Count}, DiscoveredNodes: {string.Join(", ", discoveredNodeIds)}, CurrentNodeId: {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RedisNodeDiscoveryService] 发现节点失败 - Key: {NodesHashKey}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Get node information by node ID / 根据节点 ID 获取节点信息
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <returns>Node information or null if not found / 节点信息，如果未找到则返回 null</returns>
        public async Task<NodeInfo> GetNodeInfoAsync(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return null;
            }

            try
            {
                var value = await _redisService.HashGetAsync(NodesHashKey, nodeId);

                if (string.IsNullOrEmpty(value))
                {
                    _logger.LogTrace($"[RedisNodeDiscoveryService] 节点信息未找到 - NodeId: {nodeId}, HashKey: {NodesHashKey}, CurrentNodeId: {_nodeId}");
                    return null;
                }

                var nodeInfo = JsonSerializer.Deserialize<NodeInfo>(value);
                if (nodeInfo != null)
                {
                    _logger.LogTrace($"[RedisNodeDiscoveryService] 获取节点信息成功 - NodeId: {nodeId}, HashKey: {NodesHashKey}, CurrentNodeId: {_nodeId}, LastHeartbeat: {nodeInfo.LastHeartbeat}");
                }
                else
                {
                    _logger.LogWarning($"[RedisNodeDiscoveryService] 节点信息反序列化失败 - NodeId: {nodeId}, HashKey: {NodesHashKey}, Value: {value}, CurrentNodeId: {_nodeId}");
                }

                return nodeInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RedisNodeDiscoveryService] 获取节点信息失败 - NodeId: {nodeId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                return null;
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
        /// <param name="nodeInfo">Updated node information / 更新的节点信息</param>
        public async Task UpdateNodeInfoAsync(NodeInfo nodeInfo)
        {
            if (nodeInfo == null)
            {
                throw new ArgumentNullException(nameof(nodeInfo));
            }

            try
            {
                // Update internal node info / 更新内部节点信息
                _nodeInfo = nodeInfo;
                _nodeInfo.NodeId = _nodeId; // Ensure node ID matches / 确保节点 ID 匹配
                _nodeInfo.LastHeartbeat = DateTime.UtcNow;

                var key = $"{_clusterPrefix}:nodes:{_nodeId}";
                var value = JsonSerializer.Serialize(_nodeInfo);

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

                _logger.LogDebug($"Node info updated for node {_nodeId}: ConnectionCount={nodeInfo.ConnectionCount}, CpuUsage={nodeInfo.CpuUsage}%, MemoryUsage={nodeInfo.MemoryUsage}%");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update node info for node {_nodeId}");
                throw;
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

