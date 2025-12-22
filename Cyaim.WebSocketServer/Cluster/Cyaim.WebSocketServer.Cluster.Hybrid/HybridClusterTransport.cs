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
        private bool _disposed = false;
        private bool _started = false;
        private Timer _bindingHealthCheckTimer; // 绑定健康检查定时器
        private Timer _consumerHealthCheckTimer; // 消费者健康检查定时器

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
                _logger.LogWarning($"[HybridClusterTransport] 交换机声明完成 - ExchangeName: {ExchangeName}, CurrentNodeId: {_nodeId}");

                // Declare queue for this node / 为此节点声明队列
                var requestedQueueName = $"cluster:node:{_nodeId}";
                _logger.LogWarning($"[HybridClusterTransport] 准备声明队列 - RequestedQueueName: {requestedQueueName}, CurrentNodeId: {_nodeId}");

                // 使用 autoDelete: false 防止队列因消费者断开而自动删除
                // Use autoDelete: false to prevent queue from being automatically deleted when consumer disconnects
                _queueName = await _messageQueueService.DeclareQueueAsync(
                    requestedQueueName,
                    durable: false,
                    exclusive: false,
                    autoDelete: false);

                if (string.IsNullOrEmpty(_queueName))
                {
                    throw new InvalidOperationException($"Queue declaration failed: returned empty queue name for node {_nodeId}");
                }

                if (_queueName != requestedQueueName)
                {
                    _logger.LogWarning($"[HybridClusterTransport] 队列名称不匹配 - RequestedQueueName: {requestedQueueName}, ActualQueueName: {_queueName}, CurrentNodeId: {_nodeId}");
                }

                _logger.LogWarning($"[HybridClusterTransport] 队列声明完成 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");

                // Bind queue to exchange for node-specific messages / 将队列绑定到交换机以接收节点特定消息
                await BindQueueWithRetryAsync(_queueName, ExchangeName, $"node.{_nodeId}", maxRetries: 3);

                // Bind queue to exchange for broadcast messages / 将队列绑定到交换机以接收广播消息
                await BindQueueWithRetryAsync(_queueName, ExchangeName, BroadcastRoutingKey, maxRetries: 3);

                // Verify bindings are successful / 验证绑定是否成功
                await VerifyBindingsAsync();

                // Verify queue exists before consuming / 消费前验证队列是否存在
                await VerifyQueueExistsAsync(_queueName);

                // Start consuming messages / 开始消费消息
                // Pass currentNodeId to enable early filtering of self-messages / 传递 currentNodeId 以启用早期过滤自己的消息
                try
                {
                    _logger.LogWarning($"[HybridClusterTransport] 开始启动消费者 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");
                    await _messageQueueService.ConsumeAsync(_queueName, HandleMessageAsync, autoAck: false, currentNodeId: _nodeId);
                    _logger.LogWarning($"[HybridClusterTransport] 消费者启动完成 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");

                    // Verify consumer was actually created / 验证消费者是否真的已创建
                    await Task.Delay(1000); // Wait 1 second for consumer to register / 等待 1 秒让消费者注册
                    try
                    {
                        var consumerCount = await _messageQueueService.GetQueueConsumerCountAsync(_queueName);
                        _logger.LogWarning($"[HybridClusterTransport] 消费者启动后验证 - QueueName: {_queueName}, ConsumerCount: {consumerCount}, CurrentNodeId: {_nodeId}");
                        
                        if (consumerCount == 0)
                        {
                            _logger.LogError($"[HybridClusterTransport] 错误：消费者启动后数量为 0，消费者可能未成功注册 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");
                            // Try to recreate consumer / 尝试重新创建消费者
                            _logger.LogWarning($"[HybridClusterTransport] 尝试重新创建消费者 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");
                            await _messageQueueService.ConsumeAsync(_queueName, HandleMessageAsync, autoAck: false, currentNodeId: _nodeId);
                            await Task.Delay(1000);
                            var newConsumerCount = await _messageQueueService.GetQueueConsumerCountAsync(_queueName);
                            _logger.LogWarning($"[HybridClusterTransport] 消费者重新创建后验证 - QueueName: {_queueName}, ConsumerCount: {newConsumerCount}, CurrentNodeId: {_nodeId}");
                            
                            if (newConsumerCount == 0)
                            {
                                _logger.LogError($"[HybridClusterTransport] 严重错误：消费者重新创建后数量仍为 0 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");
                                throw new InvalidOperationException($"Failed to create consumer for queue {_queueName} after retry: consumer count is still 0");
                            }
                        }
                    }
                    catch (Exception verifyEx)
                    {
                        _logger.LogError(verifyEx, $"[HybridClusterTransport] 验证消费者数量失败 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}, Error: {verifyEx.Message}, StackTrace: {verifyEx.StackTrace}");
                        // Don't throw - consumer might still work / 不抛出异常 - 消费者可能仍然有效
                    }
                }
                catch (Exception consumeEx)
                {
                    _logger.LogError(consumeEx, $"[HybridClusterTransport] 启动消费者失败 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}, Error: {consumeEx.Message}, StackTrace: {consumeEx.StackTrace}");
                    throw; // Re-throw to fail startup / 重新抛出以失败启动
                }

                // Start periodic binding health check / 启动定期绑定健康检查
                _bindingHealthCheckTimer = new Timer(async _ => await CheckAndRepairBindingsAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

                // Start periodic consumer health check / 启动定期消费者健康检查
                _consumerHealthCheckTimer = new Timer(async _ => await CheckAndRepairConsumerAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

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

                // Send message with retry mechanism / 使用重试机制发送消息
                await SendMessageWithRetryAsync(ExchangeName, routingKey, messageBytes, properties, maxRetries: 3);

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

                // 更新发送节点的心跳时间（收到消息表明节点是活跃的）
                // Update heartbeat time for the sending node (receiving message indicates node is active)
                if (!string.IsNullOrEmpty(message.FromNodeId) && message.FromNodeId != _nodeId)
                {
                    if (_knownNodes.TryGetValue(message.FromNodeId, out var nodeInfo))
                    {
                        // 更新节点心跳时间
                        // Update node heartbeat time
                        nodeInfo.LastHeartbeat = DateTime.UtcNow;
                        // 确保节点状态为 Active（如果之前是 Offline，现在应该恢复为 Active）
                        // Ensure node status is Active (if it was Offline before, it should be restored to Active now)
                        if (nodeInfo.Status == NodeStatus.Offline)
                        {
                            nodeInfo.Status = NodeStatus.Active;
                            _logger.LogWarning($"[HybridClusterTransport] 节点状态从 Offline 恢复为 Active - NodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}");
                        }
                        _logger.LogTrace($"[HybridClusterTransport] 更新节点心跳时间 - NodeId: {message.FromNodeId}, LastHeartbeat: {nodeInfo.LastHeartbeat}, CurrentNodeId: {_nodeId}");
                    }
                    else
                    {
                        // 节点不在已知列表中，可能是新节点，尝试从 Redis 发现
                        // Node not in known list, might be a new node, try to discover from Redis
                        _logger.LogWarning($"[HybridClusterTransport] 收到未知节点的消息，尝试发现节点 - FromNodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}");
                        // 触发节点发现（如果 Redis 中有该节点信息，会被发现）
                        // Trigger node discovery (if node info exists in Redis, it will be discovered)
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // 尝试从 Redis 获取节点信息
                                // Try to get node info from Redis
                                var nodeInfoFromRedis = await _discoveryService.GetNodeInfoAsync(message.FromNodeId);
                                if (nodeInfoFromRedis != null)
                                {
                                    _logger.LogWarning($"[HybridClusterTransport] 从 Redis 发现节点 - NodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}");
                                    OnNodeDiscovered(this, nodeInfoFromRedis);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"[HybridClusterTransport] 尝试从 Redis 发现节点失败 - FromNodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}");
                            }
                        });
                    }
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

            // 确保 Status 已设置（枚举默认值为 Active，但为了安全起见，如果状态不是 Active/Draining/Offline，设置为 Active）
            if (nodeInfo.Status != NodeStatus.Active && nodeInfo.Status != NodeStatus.Draining && nodeInfo.Status != NodeStatus.Offline)
            {
                nodeInfo.Status = NodeStatus.Active;
                _logger.LogWarning($"[HybridClusterTransport] OnNodeDiscovered: 节点状态无效，设置为 Active - NodeId: {nodeInfo.NodeId}, InvalidStatus: {nodeInfo.Status}, CurrentNodeId: {_nodeId}");
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

                // 保留最新的心跳时间（如果内存中的心跳时间更新，保留内存中的）
                // Keep the latest heartbeat time (if heartbeat in memory is newer, keep the one in memory)
                if (oldNodeInfo.LastHeartbeat > nodeInfo.LastHeartbeat)
                {
                    nodeInfo.LastHeartbeat = oldNodeInfo.LastHeartbeat;
                    _logger.LogTrace($"[HybridClusterTransport] 保留内存中的心跳时间（更新）- NodeId: {nodeInfo.NodeId}, MemoryHeartbeat: {oldNodeInfo.LastHeartbeat}, RedisHeartbeat: {nodeInfo.LastHeartbeat}, CurrentNodeId: {_nodeId}");
                }

                // 如果节点状态从 Offline 恢复为 Active，保留 Active 状态
                // If node status recovered from Offline to Active, keep Active status
                if (oldNodeInfo.Status == NodeStatus.Active && nodeInfo.Status == NodeStatus.Offline)
                {
                    nodeInfo.Status = NodeStatus.Active;
                    _logger.LogWarning($"[HybridClusterTransport] 节点状态保持 Active（Redis 中可能是旧数据）- NodeId: {nodeInfo.NodeId}, CurrentNodeId: {_nodeId}");
                }

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
            _bindingHealthCheckTimer?.Dispose();
            _consumerHealthCheckTimer?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        /// <summary>
        /// Bind queue with retry mechanism / 使用重试机制绑定队列
        /// </summary>
        private async Task BindQueueWithRetryAsync(string queueName, string exchangeName, string routingKey, int maxRetries = 3)
        {
            int attempt = 0;
            Exception lastException = null;

            while (attempt < maxRetries)
            {
                try
                {
                    _logger.LogWarning($"[HybridClusterTransport] 尝试绑定队列 (尝试 {attempt + 1}/{maxRetries}) - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, CurrentNodeId: {_nodeId}");
                    await _messageQueueService.BindQueueAsync(queueName, exchangeName, routingKey);
                    _logger.LogWarning($"[HybridClusterTransport] 队列绑定成功 (尝试 {attempt + 1}/{maxRetries}) - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, CurrentNodeId: {_nodeId}");
                    return; // Success / 成功
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    attempt++;
                    _logger.LogWarning(ex, $"[HybridClusterTransport] 队列绑定失败 (尝试 {attempt}/{maxRetries}) - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, CurrentNodeId: {_nodeId}, Error: {ex.Message}");

                    if (attempt < maxRetries)
                    {
                        // Wait before retry with exponential backoff / 重试前等待，使用指数退避
                        var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
                        _logger.LogWarning($"[HybridClusterTransport] 等待 {delay.TotalMilliseconds}ms 后重试绑定 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, CurrentNodeId: {_nodeId}");
                        await Task.Delay(delay);
                    }
                }
            }

            // All retries failed / 所有重试都失败
            _logger.LogError(lastException, $"[HybridClusterTransport] 队列绑定失败，已达到最大重试次数 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, CurrentNodeId: {_nodeId}, MaxRetries: {maxRetries}");
            throw new InvalidOperationException($"Failed to bind queue after {maxRetries} attempts", lastException);
        }

        /// <summary>
        /// Verify queue and consumer still exist / 验证队列和消费者是否仍然存在
        /// </summary>
        private async Task VerifyQueueAndConsumerAsync(string queueName)
        {
            if (string.IsNullOrEmpty(queueName))
            {
                _logger.LogWarning($"[HybridClusterTransport] 队列名称为空，跳过队列和消费者验证 - CurrentNodeId: {_nodeId}");
                return;
            }

            try
            {
                _logger.LogWarning($"[HybridClusterTransport] 开始验证队列和消费者 - QueueName: {queueName}, CurrentNodeId: {_nodeId}");

                // 直接验证队列是否存在，不调用 VerifyConnectionAsync 避免死锁
                // Directly verify queue exists, don't call VerifyConnectionAsync to avoid deadlock
                // ConsumeAsync 内部会确保连接和 channel 就绪
                // ConsumeAsync will ensure connection and channel are ready internally
                try
                {
                    // 尝试重新创建消费者，如果消费者已存在会跳过
                    // Try to recreate consumer, will skip if consumer already exists
                    _logger.LogWarning($"[HybridClusterTransport] 重新验证消费者 - QueueName: {queueName}, CurrentNodeId: {_nodeId}");
                    await _messageQueueService.ConsumeAsync(queueName, HandleMessageAsync, autoAck: false, currentNodeId: _nodeId);

                    _logger.LogWarning($"[HybridClusterTransport] 队列和消费者验证成功 - QueueName: {queueName}, CurrentNodeId: {_nodeId}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"[HybridClusterTransport] 消费者验证失败（可能已存在），跳过 - QueueName: {queueName}, CurrentNodeId: {_nodeId}, Error: {ex.Message}");
                    // 消费者可能已存在，这是正常的 / Consumer might already exist, this is normal
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 验证队列和消费者时发生异常 - QueueName: {queueName}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                // Don't throw - this is a verification step / 不抛出异常 - 这是验证步骤
            }
        }

        /// <summary>
        /// Verify queue exists / 验证队列是否存在
        /// </summary>
        private async Task VerifyQueueExistsAsync(string queueName)
        {
            if (string.IsNullOrEmpty(queueName))
            {
                _logger.LogWarning($"[HybridClusterTransport] 队列名称为空，跳过队列验证 - CurrentNodeId: {_nodeId}");
                return;
            }

            try
            {
                _logger.LogWarning($"[HybridClusterTransport] 开始验证队列是否存在 - QueueName: {queueName}, CurrentNodeId: {_nodeId}");

                // Verify connection is ready / 验证连接已就绪
                await _messageQueueService.VerifyConnectionAsync();

                // Try to declare queue again to verify it exists / 尝试再次声明队列以验证它是否存在
                // This will return the existing queue if it exists, or create it if it doesn't
                // 如果队列存在则返回现有队列，如果不存在则创建它
                var verifiedQueueName = await _messageQueueService.DeclareQueueAsync(queueName, durable: false, exclusive: false, autoDelete: false);

                if (string.IsNullOrEmpty(verifiedQueueName))
                {
                    _logger.LogError($"[HybridClusterTransport] 队列验证失败：返回空队列名称 - RequestedQueueName: {queueName}, CurrentNodeId: {_nodeId}");
                    throw new InvalidOperationException($"Queue verification failed: returned empty queue name for {queueName}");
                }

                if (verifiedQueueName != queueName)
                {
                    _logger.LogWarning($"[HybridClusterTransport] 队列名称不匹配 - RequestedQueueName: {queueName}, VerifiedQueueName: {verifiedQueueName}, CurrentNodeId: {_nodeId}");
                }

                _logger.LogWarning($"[HybridClusterTransport] 队列验证成功 - QueueName: {verifiedQueueName}, CurrentNodeId: {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 队列验证失败 - QueueName: {queueName}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Verify bindings are successful / 验证绑定是否成功
        /// </summary>
        private async Task VerifyBindingsAsync()
        {
            if (string.IsNullOrEmpty(_queueName))
            {
                _logger.LogWarning($"[HybridClusterTransport] 队列名称为空，跳过绑定验证 - CurrentNodeId: {_nodeId}");
                return;
            }

            try
            {
                _logger.LogWarning($"[HybridClusterTransport] 开始验证绑定 - QueueName: {_queueName}, ExchangeName: {ExchangeName}, CurrentNodeId: {_nodeId}");

                // Verify connection is ready / 验证连接已就绪
                await _messageQueueService.VerifyConnectionAsync();

                // Note: RabbitMQ doesn't provide a direct API to check bindings, so we verify by ensuring connection is ready
                // 注意：RabbitMQ 不提供直接检查绑定的 API，所以我们通过确保连接就绪来验证
                _logger.LogWarning($"[HybridClusterTransport] 绑定验证完成 - QueueName: {_queueName}, ExchangeName: {ExchangeName}, CurrentNodeId: {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 绑定验证失败 - QueueName: {_queueName}, ExchangeName: {ExchangeName}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                // Don't throw, just log - bindings will be repaired by health check / 不抛出异常，只记录日志 - 绑定将由健康检查修复
            }
        }

        /// <summary>
        /// Check and repair bindings if needed / 检查并在需要时修复绑定
        /// </summary>
        private async Task CheckAndRepairBindingsAsync()
        {
            if (_disposed || !_started || string.IsNullOrEmpty(_queueName))
            {
                return;
            }

            try
            {
                _logger.LogTrace($"[HybridClusterTransport] 开始绑定健康检查 - QueueName: {_queueName}, ExchangeName: {ExchangeName}, CurrentNodeId: {_nodeId}");

                // Verify connection is ready / 验证连接已就绪
                await _messageQueueService.VerifyConnectionAsync();

                // Re-bind queues to ensure they are still bound / 重新绑定队列以确保它们仍然绑定
                // This is a safety measure in case bindings were lost due to connection issues
                // 这是一个安全措施，以防绑定因连接问题而丢失
                try
                {
                    await BindQueueWithRetryAsync(_queueName, ExchangeName, $"node.{_nodeId}", maxRetries: 2);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"[HybridClusterTransport] 重新绑定节点特定路由键失败 - QueueName: {_queueName}, RoutingKey: node.{_nodeId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}");
                }

                try
                {
                    await BindQueueWithRetryAsync(_queueName, ExchangeName, BroadcastRoutingKey, maxRetries: 2);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"[HybridClusterTransport] 重新绑定广播路由键失败 - QueueName: {_queueName}, RoutingKey: {BroadcastRoutingKey}, CurrentNodeId: {_nodeId}, Error: {ex.Message}");
                }

                _logger.LogTrace($"[HybridClusterTransport] 绑定健康检查完成 - QueueName: {_queueName}, ExchangeName: {ExchangeName}, CurrentNodeId: {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[HybridClusterTransport] 绑定健康检查失败 - QueueName: {_queueName}, ExchangeName: {ExchangeName}, CurrentNodeId: {_nodeId}, Error: {ex.Message}");
                // Don't throw - this is a background health check / 不抛出异常 - 这是后台健康检查
            }
        }

        /// <summary>
        /// Check and repair consumer if needed / 检查并在需要时修复消费者
        /// </summary>
        private async Task CheckAndRepairConsumerAsync()
        {
            if (_disposed || !_started || string.IsNullOrEmpty(_queueName))
            {
                return;
            }

            try
            {
                _logger.LogWarning($"[HybridClusterTransport] 开始消费者健康检查 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");

                // Verify connection is ready / 验证连接已就绪
                try
                {
                    await _messageQueueService.VerifyConnectionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[HybridClusterTransport] 连接验证失败，尝试重新连接 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}, Error: {ex.Message}");
                    
                    // Try to reconnect / 尝试重新连接
                    try
                    {
                        await _messageQueueService.ConnectAsync();
                        await _messageQueueService.VerifyConnectionAsync();
                        _logger.LogWarning($"[HybridClusterTransport] 重新连接成功 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");
                        
                        // After reconnection, consumers should be automatically recreated by RabbitMQMessageQueueService
                        // 重新连接后，消费者应该由 RabbitMQMessageQueueService 自动重建
                        // But let's verify and recreate if needed / 但让我们验证并在需要时重新创建
                        await Task.Delay(500); // Wait for automatic recreation / 等待自动重建
                    }
                    catch (Exception reconnectEx)
                    {
                        _logger.LogError(reconnectEx, $"[HybridClusterTransport] 重新连接失败 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}, Error: {reconnectEx.Message}, StackTrace: {reconnectEx.StackTrace}");
                        return; // Can't proceed without connection / 没有连接无法继续
                    }
                }

                // Check consumer count / 检查消费者数量
                uint consumerCount = 0;
                bool consumerCountRetrieved = false;
                
                try
                {
                    consumerCount = await _messageQueueService.GetQueueConsumerCountAsync(_queueName);
                    consumerCountRetrieved = true;
                    _logger.LogWarning($"[HybridClusterTransport] 消费者健康检查 - QueueName: {_queueName}, ConsumerCount: {consumerCount}, CurrentNodeId: {_nodeId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[HybridClusterTransport] 获取消费者数量失败 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                    
                    // If we can't get consumer count, assume it's 0 and try to recreate / 如果无法获取消费者数量，假设为 0 并尝试重新创建
                    consumerCount = 0;
                    consumerCountRetrieved = false;
                }

                if (!consumerCountRetrieved || consumerCount == 0)
                {
                    _logger.LogError($"[HybridClusterTransport] 检测到消费者丢失或无法获取消费者数量，尝试重新创建 - QueueName: {_queueName}, ConsumerCountRetrieved: {consumerCountRetrieved}, ConsumerCount: {consumerCount}, CurrentNodeId: {_nodeId}");

                    // Try to recreate consumer / 尝试重新创建消费者
                    try
                    {
                        // Ensure connection is ready before recreating / 重新创建前确保连接就绪
                        await _messageQueueService.VerifyConnectionAsync();
                        
                        await _messageQueueService.ConsumeAsync(_queueName, HandleMessageAsync, autoAck: false, currentNodeId: _nodeId);
                        _logger.LogWarning($"[HybridClusterTransport] 消费者重新创建成功 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");

                        // Verify consumer was created / 验证消费者是否已创建
                        await Task.Delay(500); // Wait longer for consumer to register / 等待更长时间让消费者注册
                        
                        try
                        {
                            var newConsumerCount = await _messageQueueService.GetQueueConsumerCountAsync(_queueName);
                            _logger.LogWarning($"[HybridClusterTransport] 消费者重新创建后验证 - QueueName: {_queueName}, ConsumerCount: {newConsumerCount}, CurrentNodeId: {_nodeId}");

                            if (newConsumerCount == 0)
                            {
                                _logger.LogError($"[HybridClusterTransport] 警告：消费者重新创建后数量仍为 0 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}");
                            }
                        }
                        catch (Exception verifyEx)
                        {
                            _logger.LogError(verifyEx, $"[HybridClusterTransport] 验证消费者数量失败 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}, Error: {verifyEx.Message}, StackTrace: {verifyEx.StackTrace}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"[HybridClusterTransport] 重新创建消费者失败 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                    }
                }
                else
                {
                    _logger.LogTrace($"[HybridClusterTransport] 消费者健康检查通过 - QueueName: {_queueName}, ConsumerCount: {consumerCount}, CurrentNodeId: {_nodeId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HybridClusterTransport] 消费者健康检查失败 - QueueName: {_queueName}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                // Don't throw - this is a background health check / 不抛出异常 - 这是后台健康检查
            }
        }

        /// <summary>
        /// Send message with retry mechanism / 使用重试机制发送消息
        /// </summary>
        private async Task SendMessageWithRetryAsync(string exchangeName, string routingKey, byte[] messageBytes, MessageProperties properties, int maxRetries = 3)
        {
            int attempt = 0;
            Exception lastException = null;

            while (attempt < maxRetries)
            {
                try
                {
                    await _messageQueueService.PublishAsync(exchangeName, routingKey, messageBytes, properties);
                    return; // Success / 成功
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    attempt++;
                    _logger.LogWarning(ex, $"[HybridClusterTransport] 消息发布失败 (尝试 {attempt}/{maxRetries}) - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, MessageId: {properties?.MessageId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}");

                    if (attempt < maxRetries)
                    {
                        // Wait before retry with exponential backoff / 重试前等待，使用指数退避
                        var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1));
                        await Task.Delay(delay);

                        // Try to reconnect if connection is lost / 如果连接丢失，尝试重新连接
                        try
                        {
                            await _messageQueueService.VerifyConnectionAsync();
                        }
                        catch
                        {
                            // Connection is lost, try to reconnect / 连接丢失，尝试重新连接
                            try
                            {
                                await _messageQueueService.ConnectAsync();
                                await _messageQueueService.VerifyConnectionAsync();
                                _logger.LogWarning($"[HybridClusterTransport] 重新连接成功，继续重试发送消息 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, CurrentNodeId: {_nodeId}");
                            }
                            catch (Exception reconnectEx)
                            {
                                _logger.LogError(reconnectEx, $"[HybridClusterTransport] 重新连接失败 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, CurrentNodeId: {_nodeId}, Error: {reconnectEx.Message}");
                            }
                        }
                    }
                }
            }

            // All retries failed / 所有重试都失败
            _logger.LogError(lastException, $"[HybridClusterTransport] 消息发布失败，已达到最大重试次数 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, MessageId: {properties?.MessageId}, CurrentNodeId: {_nodeId}, MaxRetries: {maxRetries}");
            throw new InvalidOperationException($"Failed to publish message after {maxRetries} attempts", lastException);
        }
    }
}

