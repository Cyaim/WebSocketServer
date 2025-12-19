using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Cluster.Hybrid
{
    /// <summary>
    /// Hybrid cluster transport using Redis for service discovery and RabbitMQ for message routing
    /// 混合集群传输，使用 Redis 进行服务发现，使用 RabbitMQ 进行消息路由
    /// </summary>
    public class HybridClusterTransport : IClusterTransport
    {
        private readonly ILogger<HybridClusterTransport> _logger;
        private readonly IRedisService _redisService;
        private readonly IMessageQueueService _messageQueueService;
        private readonly RedisNodeDiscoveryService _discoveryService;
        private readonly LoadBalancer _loadBalancer;
        private readonly string _nodeId;
        private readonly NodeInfo _nodeInfo;
        private readonly ConcurrentDictionary<string, NodeInfo> _knownNodes;
        private readonly CancellationTokenSource _cancellationTokenSource;
        // 消息去重逻辑已移除，不再需要 _processedMessageIds 和 _messageIdCleanupTimer
        // Message deduplication logic removed, no longer need _processedMessageIds and _messageIdCleanupTimer
        private bool _disposed = false;
        private bool _started = false;

        private const string ExchangeName = "websocket_cluster_exchange";
        private const string BroadcastRoutingKey = "broadcast";
        private string _queueName;

        /// <summary>
        /// Event triggered when message received / 消息接收时触发的事件
        /// </summary>
        public event EventHandler<ClusterMessageEventArgs> MessageReceived;

        /// <summary>
        /// Event triggered when node connected / 节点连接时触发的事件
        /// </summary>
        public event EventHandler<ClusterNodeEventArgs> NodeConnected;

        /// <summary>
        /// Event triggered when node disconnected / 节点断开连接时触发的事件
        /// </summary>
        public event EventHandler<ClusterNodeEventArgs> NodeDisconnected;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="loggerFactory">Logger factory for creating specific loggers / 用于创建特定 logger 的 logger factory</param>
        /// <param name="redisService">Redis service / Redis 服务</param>
        /// <param name="messageQueueService">Message queue service / 消息队列服务</param>
        /// <param name="nodeId">Current node ID / 当前节点 ID</param>
        /// <param name="nodeInfo">Current node information / 当前节点信息</param>
        /// <param name="loadBalancingStrategy">Load balancing strategy / 负载均衡策略</param>
        /// <param name="nodeInfoProvider">Optional function to automatically get latest node info during heartbeat / 可选函数，用于在心跳时自动获取最新节点信息</param>
        public HybridClusterTransport(
            ILogger<HybridClusterTransport> logger,
            ILoggerFactory loggerFactory,
            IRedisService redisService,
            IMessageQueueService messageQueueService,
            string nodeId,
            NodeInfo nodeInfo,
            LoadBalancingStrategy loadBalancingStrategy = LoadBalancingStrategy.LeastConnections,
            Func<Task<NodeInfo>> nodeInfoProvider = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _redisService = redisService ?? throw new ArgumentNullException(nameof(redisService));
            _messageQueueService = messageQueueService ?? throw new ArgumentNullException(nameof(messageQueueService));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _nodeInfo = nodeInfo ?? throw new ArgumentNullException(nameof(nodeInfo));

            _knownNodes = new ConcurrentDictionary<string, NodeInfo>();
            _cancellationTokenSource = new CancellationTokenSource();
            // 消息去重逻辑已移除，不再需要 _processedMessageIds
            // Message deduplication logic removed, no longer need _processedMessageIds

            // Create specific loggers using logger factory / 使用 logger factory 创建特定类型的 logger
            var discoveryLogger = loggerFactory.CreateLogger<RedisNodeDiscoveryService>();
            var loadBalancerLogger = loggerFactory.CreateLogger<LoadBalancer>();

            // If no provider is provided, try to create a default one that auto-detects connection count
            // 如果没有提供 provider，尝试创建一个默认的，自动检测连接数
            Func<Task<NodeInfo>> finalProvider = nodeInfoProvider;
            if (finalProvider == null)
            {
                finalProvider = CreateDefaultNodeInfoProvider(nodeInfo);
            }

            _discoveryService = new RedisNodeDiscoveryService(
                discoveryLogger,
                redisService,
                nodeId,
                nodeInfo,
                clusterPrefix: "websocket:cluster",
                nodeInfoProvider: finalProvider);

            _loadBalancer = new LoadBalancer(loadBalancerLogger, loadBalancingStrategy);

            // Subscribe to discovery events / 订阅发现事件
            _discoveryService.NodeDiscovered += OnNodeDiscovered;
            _discoveryService.NodeRemoved += OnNodeRemoved;

            // 消息去重逻辑已移除，不再需要消息ID清理定时器
            // Message deduplication logic removed, no longer need message ID cleanup timer
        }

        /// <summary>
        /// Start the transport service / 启动传输服务
        /// </summary>
        public async Task StartAsync()
        {
            if (_started)
            {
                _logger.LogWarning($"Transport for node {_nodeId} is already started");
                return;
            }

            _logger.LogWarning($"[HybridClusterTransport] 启动混合集群传输 - NodeId: {_nodeId}, Address: {_nodeInfo.Address}, Port: {_nodeInfo.Port}, Endpoint: {_nodeInfo.Endpoint}");

            try
            {
                // Connect to Redis / 连接到 Redis
                await _redisService.ConnectAsync();

                // Start discovery service / 启动发现服务
                await _discoveryService.StartAsync();

                // Connect to message queue / 连接到消息队列
                await _messageQueueService.ConnectAsync();

                // Verify connection is ready before proceeding / 验证连接已就绪再继续
                // This ensures channel is fully established before RaftNode starts / 这确保在 RaftNode 启动前 channel 已完全建立
                int verifyAttempts = 0;
                while (verifyAttempts < 10) // Try up to 10 times / 最多尝试 10 次
                {
                    try
                    {
                        // Try to ensure connection is ready / 尝试确保连接已就绪
                        await _messageQueueService.VerifyConnectionAsync();
                        break; // Connection verified / 连接已验证
                    }
                    catch (Exception ex)
                    {
                        verifyAttempts++;
                        if (verifyAttempts >= 10)
                        {
                            _logger.LogError(ex, $"[HybridClusterTransport] 验证 RabbitMQ 连接失败，已达到最大重试次数 - NodeId: {_nodeId}");
                            throw;
                        }
                        _logger.LogWarning($"[HybridClusterTransport] 验证 RabbitMQ 连接失败，重试中 ({verifyAttempts}/10) - NodeId: {_nodeId}, Error: {ex.Message}");
                        await Task.Delay(200); // Wait 200ms before retry / 重试前等待 200ms
                    }
                }

                // Declare exchange / 声明交换机
                await _messageQueueService.DeclareExchangeAsync(ExchangeName, "topic", durable: true);

                // Declare queue for this node / 为此节点声明队列
                _queueName = await _messageQueueService.DeclareQueueAsync(
                    $"cluster:node:{_nodeId}",
                    durable: false,
                    exclusive: false,
                    autoDelete: true);

                // Bind queue to exchange for node-specific messages / 将队列绑定到交换机以接收节点特定消息
                await _messageQueueService.BindQueueAsync(_queueName, ExchangeName, $"node.{_nodeId}");

                // Bind queue to exchange for broadcast messages / 将队列绑定到交换机以接收广播消息
                await _messageQueueService.BindQueueAsync(_queueName, ExchangeName, BroadcastRoutingKey);

                // Start consuming messages / 开始消费消息
                // Pass currentNodeId to enable early filtering of self-messages / 传递 currentNodeId 以启用早期过滤自己的消息
                await _messageQueueService.ConsumeAsync(_queueName, HandleMessageAsync, autoAck: false, currentNodeId: _nodeId);

                // Wait a bit for initial node discovery / 等待初始节点发现
                _logger.LogWarning($"[HybridClusterTransport] 等待初始节点发现... - NodeId: {_nodeId}");
                await Task.Delay(2000); // Wait 2 seconds for initial discovery / 等待 2 秒进行初始发现

                // Log discovered nodes / 记录发现的节点
                var discoveredNodes = GetKnownNodeIds();
                _logger.LogWarning($"[HybridClusterTransport] 初始节点发现完成 - NodeId: {_nodeId}, DiscoveredNodeCount: {discoveredNodes.Count}, DiscoveredNodes: {string.Join(", ", discoveredNodes)}");

                _started = true;
                _logger.LogWarning($"[HybridClusterTransport] 混合集群传输启动成功 - NodeId: {_nodeId}, QueueName: {_queueName}, ExchangeName: {ExchangeName}, KnownNodes: {discoveredNodes.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start hybrid cluster transport for node {_nodeId}");
                throw;
            }
        }

        /// <summary>
        /// Stop the transport service / 停止传输服务
        /// </summary>
        public async Task StopAsync()
        {
            if (!_started)
            {
                return;
            }

            _logger.LogInformation($"Stopping hybrid cluster transport for node {_nodeId}");

            _cancellationTokenSource.Cancel();

            try
            {
                // 消息去重逻辑已移除，不再需要清理定时器
                // Message deduplication logic removed, no longer need cleanup timer
                await _discoveryService.StopAsync();
                await _messageQueueService.DisconnectAsync();
                await _redisService.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error stopping hybrid cluster transport for node {_nodeId}");
            }

            _started = false;
            _logger.LogInformation($"Hybrid cluster transport stopped for node {_nodeId}");
        }

        /// <summary>
        /// Send message to specific node / 向指定节点发送消息
        /// </summary>
        /// <param name="nodeId">Target node ID / 目标节点 ID</param>
        /// <param name="message">Message to send / 要发送的消息</param>
        public async Task SendAsync(string nodeId, ClusterMessage message)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                throw new ArgumentNullException(nameof(nodeId));
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            try
            {
                message.FromNodeId = _nodeId;
                message.ToNodeId = nodeId;

                // Ensure MessageId is set for deduplication / 确保设置 MessageId 以便去重
                // If MessageId is empty, generate a unique one based on node and timestamp
                // 如果 MessageId 为空，基于节点和时间戳生成唯一ID
                if (string.IsNullOrEmpty(message.MessageId))
                {
                    message.MessageId = $"{_nodeId}:{nodeId}:{DateTime.UtcNow.Ticks}:{Guid.NewGuid():N}";
                }

                // Ensure Timestamp is set / 确保设置时间戳
                if (message.Timestamp == default)
                {
                    message.Timestamp = DateTime.UtcNow;
                }

                var routingKey = $"node.{nodeId}";
                var messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

                var properties = new MessageProperties
                {
                    MessageId = message.MessageId,
                    CorrelationId = message.MessageId,
                    Timestamp = message.Timestamp
                };

                await _messageQueueService.PublishAsync(ExchangeName, routingKey, messageBytes, properties);

                _logger.LogWarning($"[HybridClusterTransport] 消息发送成功 - TargetNodeId: {nodeId}, MessageId: {message.MessageId}, MessageType: {message.Type}, RoutingKey: {routingKey}, ExchangeName: {ExchangeName}, CurrentNodeId: {_nodeId}, MessageSize: {messageBytes.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 消息发送失败 - TargetNodeId: {nodeId}, MessageId: {message.MessageId}, MessageType: {message.Type}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Broadcast message to all nodes / 向所有节点广播消息
        /// </summary>
        /// <param name="message">Message to broadcast / 要广播的消息</param>
        public async Task BroadcastAsync(ClusterMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            try
            {
                message.FromNodeId = _nodeId;
                message.ToNodeId = null; // Broadcast / 广播

                // Ensure MessageId is set for deduplication / 确保设置 MessageId 以便去重
                // If MessageId is empty, generate a unique one
                // 如果 MessageId 为空，生成唯一ID
                if (string.IsNullOrEmpty(message.MessageId))
                {
                    message.MessageId = $"{_nodeId}:broadcast:{DateTime.UtcNow.Ticks}:{Guid.NewGuid():N}";
                }

                // Ensure Timestamp is set / 确保设置时间戳
                if (message.Timestamp == default)
                {
                    message.Timestamp = DateTime.UtcNow;
                }

                var messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

                var properties = new MessageProperties
                {
                    MessageId = message.MessageId,
                    CorrelationId = message.MessageId,
                    Timestamp = message.Timestamp,
                    Headers = new Dictionary<string, object>
                    {
                        { "FromNodeId", _nodeId } // Add FromNodeId header to filter self-messages early / 添加 FromNodeId header 以便早期过滤自己的消息
                    }
                };

                // Only broadcast if there are other nodes (excluding self) / 只有在有其他节点时才广播（排除自己）
                // Note: _knownNodes does not include self, so _knownNodes.Count is the count of other nodes
                // 注意：_knownNodes 不包含自己，所以 _knownNodes.Count 就是其他节点的数量
                var otherNodesCount = _knownNodes.Count;
                if (otherNodesCount > 0)
                {
                    await _messageQueueService.PublishAsync(ExchangeName, BroadcastRoutingKey, messageBytes, properties);
                    _logger.LogTrace($"[HybridClusterTransport] 广播消息成功 - MessageId: {message.MessageId}, MessageType: {message.Type}, CurrentNodeId: {_nodeId}, MessageSize: {messageBytes.Length} bytes, 已知节点数: {_knownNodes.Count}, 其他节点数: {otherNodesCount}");
                }
                else
                {
                    _logger.LogTrace($"[HybridClusterTransport] 跳过广播消息（没有其他节点）- MessageId: {message.MessageId}, MessageType: {message.Type}, CurrentNodeId: {_nodeId}, 已知节点数: {_knownNodes.Count}, 其他节点数: {otherNodesCount}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 广播消息失败 - MessageId: {message.MessageId}, MessageType: {message.Type}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Send message to multiple nodes / 向多个节点发送消息
        /// </summary>
        /// <param name="nodeIds">Target node IDs / 目标节点 ID 列表</param>
        /// <param name="message">Message to send / 要发送的消息</param>
        /// <returns>Dictionary of node ID and send result / 节点ID和发送结果的字典</returns>
        /// <remarks>
        /// This method sends the same message to multiple nodes. Each node will receive a copy of the message.
        /// If you want to send different messages to different nodes, call SendAsync multiple times.
        /// 此方法向多个节点发送相同的消息。每个节点都会收到消息的副本。
        /// 如果要向不同节点发送不同的消息，请多次调用 SendAsync。
        /// </remarks>
        public async Task<Dictionary<string, bool>> SendToMultipleNodesAsync(IEnumerable<string> nodeIds, ClusterMessage message)
        {
            if (nodeIds == null)
            {
                throw new ArgumentNullException(nameof(nodeIds));
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var results = new Dictionary<string, bool>();
            var tasks = new List<Task>();

            foreach (var nodeId in nodeIds)
            {
                if (string.IsNullOrEmpty(nodeId))
                {
                    continue;
                }

                var task = Task.Run(async () =>
                {
                    try
                    {
                        // Create a copy of the message for each node to ensure unique MessageId per node
                        // 为每个节点创建消息副本，确保每个节点都有唯一的 MessageId
                        var nodeMessage = new ClusterMessage
                        {
                            Type = message.Type,
                            FromNodeId = message.FromNodeId,
                            ToNodeId = nodeId,
                            Payload = message.Payload,
                            MessageId = message.MessageId, // Will be auto-generated if empty / 如果为空将自动生成
                            Timestamp = message.Timestamp
                        };

                        await SendAsync(nodeId, nodeMessage);
                        results[nodeId] = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to send message to node {nodeId}");
                        results[nodeId] = false;
                    }
                });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
            return results;
        }

        /// <summary>
        /// Check if node is connected / 检查节点是否已连接
        /// </summary>
        /// <param name="nodeId">Node ID to check / 要检查的节点 ID</param>
        /// <returns>True if connected, false otherwise / 已连接返回 true，否则返回 false</returns>
        public bool IsNodeConnected(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                _logger.LogWarning($"[HybridClusterTransport] IsNodeConnected: 节点ID为空 - CurrentNodeId: {_nodeId}");
                return false;
            }

            if (nodeId == _nodeId)
            {
                return true; // Current node is always "connected" / 当前节点始终"已连接"
            }

            var exists = _knownNodes.TryGetValue(nodeId, out var nodeInfo);
            if (!exists)
            {
                _logger.LogWarning($"[HybridClusterTransport] IsNodeConnected: 节点不在已知节点列表中 - TargetNodeId: {nodeId}, CurrentNodeId: {_nodeId}, KnownNodeCount: {_knownNodes.Count}, KnownNodes: {string.Join(", ", _knownNodes.Keys)}");
                return false;
            }

            var isActive = nodeInfo.Status == NodeStatus.Active;
            var timeSinceHeartbeat = DateTime.UtcNow - nodeInfo.LastHeartbeat;
            var isHeartbeatRecent = timeSinceHeartbeat < TimeSpan.FromSeconds(60);

            var isConnected = isActive && isHeartbeatRecent;

            _logger.LogWarning($"[HybridClusterTransport] IsNodeConnected: 节点连接状态检查 - TargetNodeId: {nodeId}, CurrentNodeId: {_nodeId}, Exists: {exists}, Status: {nodeInfo.Status}, IsActive: {isActive}, TimeSinceHeartbeat: {timeSinceHeartbeat.TotalSeconds}秒, IsHeartbeatRecent: {isHeartbeatRecent}, IsConnected: {isConnected}");

            return isConnected;
        }

        /// <summary>
        /// Get optimal node for load balancing / 获取用于负载均衡的最优节点
        /// </summary>
        /// <param name="excludeNodeId">Node ID to exclude / 要排除的节点 ID</param>
        /// <returns>Optimal node info or null / 最优节点信息或 null</returns>
        public NodeInfo GetOptimalNode(string excludeNodeId = null)
        {
            var nodes = new List<NodeInfo>(_knownNodes.Values);
            return _loadBalancer.SelectNode(nodes, excludeNodeId ?? _nodeId);
        }

        /// <summary>
        /// Get all known node IDs / 获取所有已知节点 ID
        /// </summary>
        /// <returns>List of known node IDs / 已知节点 ID 列表</returns>
        public List<string> GetKnownNodeIds()
        {
            var nodeIds = new List<string>(_knownNodes.Keys);
            _logger.LogWarning($"[HybridClusterTransport] 获取已知节点ID - CurrentNodeId: {_nodeId}, KnownNodeCount: {nodeIds.Count}, NodeIds: {string.Join(", ", nodeIds)}");
            return nodeIds;
        }

        /// <summary>
        /// Store connection route information using Redis
        /// 使用 Redis 存储连接路由信息
        /// </summary>
        public async Task<bool> StoreConnectionRouteAsync(string connectionId, string nodeId, Dictionary<string, string> metadata = null)
        {
            try
            {
                // Redis key 前缀
                const string routeKeyPrefix = "websocket:cluster:connections:";
                const string metadataKeyPrefix = "websocket:cluster:connection:metadata:";

                // 存储连接路由：connectionId -> nodeId
                var routeKey = $"{routeKeyPrefix}{connectionId}";
                await _redisService.SetAsync(routeKey, nodeId, TimeSpan.FromHours(24)); // 24小时过期
                _logger.LogWarning($"[HybridClusterTransport] 连接路由已存储到 Redis - ConnectionId: {connectionId}, NodeId: {nodeId}, Key: {routeKey}");

                // 存储连接元数据（如果提供）
                if (metadata != null && metadata.Count > 0)
                {
                    var metadataKey = $"{metadataKeyPrefix}{connectionId}";
                    var metadataJson = JsonSerializer.Serialize(metadata);
                    await _redisService.SetAsync(metadataKey, metadataJson, TimeSpan.FromHours(24));
                    _logger.LogWarning($"[HybridClusterTransport] 连接元数据已存储到 Redis - ConnectionId: {connectionId}, Key: {metadataKey}");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 存储连接路由到 Redis 失败 - ConnectionId: {connectionId}, NodeId: {nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Get connection route information from Redis
        /// 从 Redis 获取连接路由信息
        /// </summary>
        public async Task<string> GetConnectionRouteAsync(string connectionId)
        {
            try
            {
                const string routeKeyPrefix = "websocket:cluster:connections:";
                var routeKey = $"{routeKeyPrefix}{connectionId}";
                var nodeId = await _redisService.GetAsync(routeKey);

                if (!string.IsNullOrEmpty(nodeId))
                {
                    _logger.LogWarning($"[HybridClusterTransport] 从 Redis 查询连接路由成功 - ConnectionId: {connectionId}, NodeId: {nodeId}, CurrentNodeId: {_nodeId}");
                    return nodeId;
                }
                else
                {
                    _logger.LogWarning($"[HybridClusterTransport] 从 Redis 查询连接路由未找到 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 从 Redis 查询连接路由失败 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Remove connection route information from Redis
        /// 从 Redis 删除连接路由信息
        /// </summary>
        public async Task<bool> RemoveConnectionRouteAsync(string connectionId)
        {
            try
            {
                const string routeKeyPrefix = "websocket:cluster:connections:";
                const string metadataKeyPrefix = "websocket:cluster:connection:metadata:";

                var routeKey = $"{routeKeyPrefix}{connectionId}";
                var metadataKey = $"{metadataKeyPrefix}{connectionId}";

                await _redisService.DeleteAsync(routeKey);
                await _redisService.DeleteAsync(metadataKey);

                _logger.LogWarning($"[HybridClusterTransport] 连接路由已从 Redis 删除 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 从 Redis 删除连接路由失败 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Refresh connection route expiration in Redis
        /// 刷新 Redis 中的连接路由过期时间
        /// </summary>
        public async Task<bool> RefreshConnectionRouteAsync(string connectionId, string nodeId)
        {
            try
            {
                const string routeKeyPrefix = "websocket:cluster:connections:";
                const string metadataKeyPrefix = "websocket:cluster:connection:metadata:";

                var routeKey = $"{routeKeyPrefix}{connectionId}";
                var metadataKey = $"{metadataKeyPrefix}{connectionId}";

                // Check if route exists / 检查路由是否存在
                var existingRoute = await _redisService.GetAsync(routeKey);
                if (string.IsNullOrEmpty(existingRoute))
                {
                    _logger.LogWarning($"[HybridClusterTransport] 连接路由不存在，无法刷新 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}");
                    return false;
                }

                // Verify node ID matches / 验证节点 ID 是否匹配
                if (existingRoute != nodeId)
                {
                    _logger.LogWarning($"[HybridClusterTransport] 连接路由节点 ID 不匹配，无法刷新 - ConnectionId: {connectionId}, ExpectedNodeId: {nodeId}, ActualNodeId: {existingRoute}, CurrentNodeId: {_nodeId}");
                    return false;
                }

                // Refresh expiration by setting the key again with new expiration / 通过重新设置键来刷新过期时间
                await _redisService.SetAsync(routeKey, nodeId, TimeSpan.FromHours(24)); // 重新设置 24 小时过期

                // Also refresh metadata expiration if it exists / 如果元数据存在，也刷新其过期时间
                var existingMetadata = await _redisService.GetAsync(metadataKey);
                if (!string.IsNullOrEmpty(existingMetadata))
                {
                    await _redisService.SetAsync(metadataKey, existingMetadata, TimeSpan.FromHours(24));
                }

                _logger.LogWarning($"[HybridClusterTransport] 连接路由过期时间已刷新 - ConnectionId: {connectionId}, NodeId: {nodeId}, CurrentNodeId: {_nodeId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 刷新连接路由过期时间失败 - ConnectionId: {connectionId}, NodeId: {nodeId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Update current node information in Redis / 更新 Redis 中的当前节点信息
        /// </summary>
        /// <param name="nodeInfo">Updated node information / 更新的节点信息</param>
        /// <remarks>
        /// This method allows you to update dynamic node information such as connection count, CPU usage, and memory usage.
        /// The updated information will be stored in Redis and used for load balancing.
        /// 此方法允许您更新动态节点信息，如连接数、CPU 使用率和内存使用率。
        /// 更新的信息将存储在 Redis 中，并用于负载均衡。
        /// </remarks>
        public async Task UpdateNodeInfoAsync(NodeInfo nodeInfo)
        {
            if (nodeInfo == null)
            {
                throw new ArgumentNullException(nameof(nodeInfo));
            }

            // Update internal node info / 更新内部节点信息
            _nodeInfo.NodeId = nodeInfo.NodeId;
            _nodeInfo.Address = nodeInfo.Address;
            _nodeInfo.Port = nodeInfo.Port;
            _nodeInfo.Endpoint = nodeInfo.Endpoint;
            _nodeInfo.ConnectionCount = nodeInfo.ConnectionCount;
            _nodeInfo.MaxConnections = nodeInfo.MaxConnections;
            _nodeInfo.CpuUsage = nodeInfo.CpuUsage;
            _nodeInfo.MemoryUsage = nodeInfo.MemoryUsage;
            _nodeInfo.Status = nodeInfo.Status;
            if (nodeInfo.Metadata != null)
            {
                _nodeInfo.Metadata = nodeInfo.Metadata;
            }

            // Update in Redis via discovery service / 通过发现服务更新到 Redis
            await _discoveryService.UpdateNodeInfoAsync(_nodeInfo);
        }

        /// <summary>
        /// Handle incoming message / 处理传入消息
        /// </summary>
        private async Task<bool> HandleMessageAsync(byte[] body, MessageProperties properties)
        {
            try
            {
                _logger.LogWarning($"[HybridClusterTransport] HandleMessageAsync 开始处理 - CurrentNodeId: {_nodeId}, BodyLength: {body.Length}, MessageId: {properties.MessageId}, CorrelationId: {properties.CorrelationId}");

                var messageJson = Encoding.UTF8.GetString(body);
                _logger.LogWarning($"[HybridClusterTransport] 消息JSON解析 - CurrentNodeId: {_nodeId}, MessageJsonLength: {messageJson.Length}, First100Chars: {(messageJson.Length > 100 ? messageJson.Substring(0, 100) : messageJson)}");

                var message = JsonSerializer.Deserialize<ClusterMessage>(messageJson);

                if (message == null)
                {
                    _logger.LogWarning($"[HybridClusterTransport] 收到空或无效消息 - CurrentNodeId: {_nodeId}, MessageJson: {messageJson}");
                    return true; // Ack to remove from queue / 确认以从队列中移除
                }

                _logger.LogTrace($"[HybridClusterTransport] 收到集群消息 - MessageType: {message.Type}, FromNodeId: {message.FromNodeId}, ToNodeId: {message.ToNodeId}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}, MessageSize: {body.Length} bytes");

                // Ignore messages from self FIRST to avoid processing own messages / 首先忽略来自自己的消息，避免处理自己的消息
                if (message.FromNodeId == _nodeId)
                {
                    _logger.LogWarning($"[HybridClusterTransport] 忽略来自自己的消息 - MessageType: {message.Type}, MessageId: {message.MessageId}, FromNodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}");
                    return true;
                }

                // 特别记录查询消息的接收，便于调试
                if (message.Type == ClusterMessageType.QueryWebSocketConnection)
                {
                    _logger.LogWarning($"[HybridClusterTransport] 收到查询连接消息 - FromNodeId: {message.FromNodeId}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}, Payload: {message.Payload}");
                }

                // Check if message is for this node / 检查消息是否发送给当前节点
                // If ToNodeId is null, it's a broadcast message - all nodes should process it
                // If ToNodeId is set, only the target node should process it
                // 如果 ToNodeId 为 null，这是广播消息 - 所有节点都应该处理
                // 如果 ToNodeId 已设置，只有目标节点应该处理

                // #region agent log
                try
                {
                    var logData = new
                    {
                        location = "HybridClusterTransport.cs:502",
                        message = "Checking if message is for this node",
                        data = new
                        {
                            messageType = message.Type.ToString(),
                            fromNodeId = message.FromNodeId,
                            toNodeId = message.ToNodeId ?? "null",
                            messageId = message.MessageId,
                            currentNodeId = _nodeId,
                            isToNodeIdEmpty = string.IsNullOrEmpty(message.ToNodeId),
                            isToNodeIdMatch = message.ToNodeId == _nodeId,
                            willIgnore = !string.IsNullOrEmpty(message.ToNodeId) && message.ToNodeId != _nodeId
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        sessionId = "debug-session",
                        runId = "run1",
                        hypothesisId = "B"
                    };
                    var logJson = JsonSerializer.Serialize(logData);
                    System.IO.File.AppendAllText(@"e:\OneDrive\Work\WorkSpaces\.cursor\debug.log", logJson + Environment.NewLine);
                }
                catch { }
                // #endregion

                if (!string.IsNullOrEmpty(message.ToNodeId) && message.ToNodeId != _nodeId)
                {
                    // This message is for another node, ignore it / 此消息是发送给其他节点的，忽略它
                    // Note: This is expected behavior for targeted messages. Use BroadcastAsync to send to all nodes.
                    // 注意：这是定向消息的预期行为。使用 BroadcastAsync 可以向所有节点发送消息。
                    _logger.LogWarning($"[HybridClusterTransport] 忽略消息（目标节点不匹配）- MessageId: {message.MessageId}, MessageType: {message.Type}, TargetNodeId: {message.ToNodeId}, CurrentNodeId: {_nodeId}");
                    return true; // Ack to remove from queue / 确认以从队列中移除
                }

                // 消息去重逻辑已移除 - 按用户要求移除过滤相同消息ID的逻辑
                // Message deduplication logic removed - removed filtering of messages with same ID as per user request

                // Trigger message received event / 触发消息接收事件
                _logger.LogWarning($"[HybridClusterTransport] 触发消息接收事件 - MessageType: {message.Type}, FromNodeId: {message.FromNodeId}, ToNodeId: {message.ToNodeId}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}");


                // Trigger message received event / 触发消息接收事件
                _logger.LogTrace($"[HybridClusterTransport] 触发消息接收事件 - MessageType: {message.Type}, FromNodeId: {message.FromNodeId}, ToNodeId: {message.ToNodeId}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}");
                MessageReceived?.Invoke(this, new ClusterMessageEventArgs
                {
                    FromNodeId = message.FromNodeId,
                    Message = message
                });

                if (properties.DeliveryTag > 0)
                {
                    await _messageQueueService.AckAsync(properties.DeliveryTag);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling message");
                return false; // Don't ack, message will be requeued / 不确认，消息将重新入队
            }
        }

        // 消息去重逻辑已移除，不再需要清理方法
        // Message deduplication logic removed, no longer need cleanup method

        /// <summary>
        /// Handle node discovered event / 处理节点发现事件
        /// </summary>
        private void OnNodeDiscovered(object sender, NodeInfo nodeInfo)
        {
            if (nodeInfo == null || nodeInfo.NodeId == _nodeId)
            {
                _logger.LogWarning($"[HybridClusterTransport] OnNodeDiscovered: 跳过无效节点或自己的节点 - NodeId: {nodeInfo?.NodeId ?? "null"}, CurrentNodeId: {_nodeId}");
                return;
            }

            // 确保 LastHeartbeat 已设置
            if (nodeInfo.LastHeartbeat == default)
            {
                nodeInfo.LastHeartbeat = DateTime.UtcNow;
                _logger.LogWarning($"[HybridClusterTransport] OnNodeDiscovered: 节点心跳时间未设置，使用当前时间 - NodeId: {nodeInfo.NodeId}, CurrentNodeId: {_nodeId}");
            }

            // 确保 Status 已设置
            if (nodeInfo.Status == NodeStatus.Unknown)
            {
                nodeInfo.Status = NodeStatus.Active;
                _logger.LogWarning($"[HybridClusterTransport] OnNodeDiscovered: 节点状态未设置，设置为 Active - NodeId: {nodeInfo.NodeId}, CurrentNodeId: {_nodeId}");
            }

            var wasNew = _knownNodes.TryAdd(nodeInfo.NodeId, nodeInfo);
            if (wasNew)
            {
                _logger.LogWarning($"[HybridClusterTransport] 发现新节点 - NodeId: {nodeInfo.NodeId}, Address: {nodeInfo.Address}, Port: {nodeInfo.Port}, Status: {nodeInfo.Status}, LastHeartbeat: {nodeInfo.LastHeartbeat}, CurrentNodeId: {_nodeId}, 已知节点数: {_knownNodes.Count}");
                NodeConnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeInfo.NodeId });
            }
            else
            {
                // Update existing node info / 更新现有节点信息
                var oldNodeInfo = _knownNodes[nodeInfo.NodeId];
                _knownNodes[nodeInfo.NodeId] = nodeInfo;
                _logger.LogWarning($"[HybridClusterTransport] 更新现有节点信息 - NodeId: {nodeInfo.NodeId}, OldStatus: {oldNodeInfo.Status}, NewStatus: {nodeInfo.Status}, OldLastHeartbeat: {oldNodeInfo.LastHeartbeat}, NewLastHeartbeat: {nodeInfo.LastHeartbeat}, CurrentNodeId: {_nodeId}, 已知节点数: {_knownNodes.Count}");
            }
        }

        /// <summary>
        /// Handle node removed event / 处理节点移除事件
        /// </summary>
        private void OnNodeRemoved(object sender, string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || nodeId == _nodeId)
            {
                return;
            }

            if (_knownNodes.TryRemove(nodeId, out _))
            {
                _logger.LogWarning($"[HybridClusterTransport] 节点从集群中移除 - NodeId: {nodeId}, CurrentNodeId: {_nodeId}, 剩余节点数: {_knownNodes.Count}");
                NodeDisconnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });
            }
        }

        /// <summary>
        /// Create default node info provider that auto-detects connection count
        /// 创建默认的节点信息提供者，自动检测连接数
        /// </summary>
        /// <param name="baseNodeInfo">Base node info to use / 要使用的基础节点信息</param>
        /// <returns>Node info provider function / 节点信息提供者函数</returns>
        private Func<Task<NodeInfo>> CreateDefaultNodeInfoProvider(NodeInfo baseNodeInfo)
        {
            return async () =>
            {
                try
                {
                    // Try to get connection count from MvcChannelHandler (most common case)
                    // 尝试从 MvcChannelHandler 获取连接数（最常见的情况）
                    int connectionCount = 0;
                    try
                    {
                        // Use reflection to avoid direct dependency / 使用反射避免直接依赖
                        var mvcHandlerType = Type.GetType("Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler.MvcChannelHandler, Cyaim.WebSocketServer");
                        if (mvcHandlerType != null)
                        {
                            var clientsProperty = mvcHandlerType.GetProperty("Clients", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                            if (clientsProperty != null)
                            {
                                var clients = clientsProperty.GetValue(null);
                                if (clients is System.Collections.ICollection collection)
                                {
                                    connectionCount = collection.Count;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore reflection errors / 忽略反射错误
                    }

                    // Try to get from ClusterManager if available / 如果可用，尝试从 ClusterManager 获取
                    if (connectionCount == 0)
                    {
                        try
                        {
                            var clusterCenterType = Type.GetType("Cyaim.WebSocketServer.Infrastructure.Cluster.GlobalClusterCenter, Cyaim.WebSocketServer");
                            if (clusterCenterType != null)
                            {
                                var clusterManagerProperty = clusterCenterType.GetProperty("ClusterManager", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                if (clusterManagerProperty != null)
                                {
                                    var clusterManager = clusterManagerProperty.GetValue(null);
                                    if (clusterManager != null)
                                    {
                                        var getLocalConnectionCountMethod = clusterManager.GetType().GetMethod("GetLocalConnectionCount");
                                        if (getLocalConnectionCountMethod != null)
                                        {
                                            var count = getLocalConnectionCountMethod.Invoke(clusterManager, null);
                                            if (count is int localCount)
                                            {
                                                connectionCount = localCount;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Ignore reflection errors / 忽略反射错误
                        }
                    }

                    // Get CPU and memory usage / 获取 CPU 和内存使用率
                    double cpuUsage = 0.0;
                    double memoryUsage = 0.0;
                    try
                    {
                        var process = System.Diagnostics.Process.GetCurrentProcess();
                        var totalProcessorTime = process.TotalProcessorTime.TotalMilliseconds;
                        var uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
                        cpuUsage = uptime > 0 ? (totalProcessorTime / uptime) * 100 : 0.0;

                        var workingSet = process.WorkingSet64;
                        memoryUsage = (double)workingSet / (1024 * 1024); // Convert to MB / 转换为 MB
                    }
                    catch
                    {
                        // Ignore errors / 忽略错误
                    }

                    // Return updated node info / 返回更新的节点信息
                    return new NodeInfo
                    {
                        NodeId = baseNodeInfo.NodeId,
                        Address = baseNodeInfo.Address,
                        Port = baseNodeInfo.Port,
                        Endpoint = baseNodeInfo.Endpoint,
                        ConnectionCount = connectionCount,
                        MaxConnections = baseNodeInfo.MaxConnections,
                        CpuUsage = cpuUsage,
                        MemoryUsage = memoryUsage,
                        Status = baseNodeInfo.Status,
                        Metadata = baseNodeInfo.Metadata ?? new Dictionary<string, string>(),
                        RegisteredAt = baseNodeInfo.RegisteredAt
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to auto-update node info, using cached info");
                    // Return base info with updated heartbeat / 返回基础信息并更新心跳
                    baseNodeInfo.LastHeartbeat = DateTime.UtcNow;
                    return baseNodeInfo;
                }
            };
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

            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during disposal");
            }

            _discoveryService?.Dispose();
            // 消息去重逻辑已移除，不再需要清理定时器
            // Message deduplication logic removed, no longer need cleanup timer
            _cancellationTokenSource?.Dispose();
        }
    }
}

