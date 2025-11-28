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
        public HybridClusterTransport(
            ILogger<HybridClusterTransport> logger,
            ILoggerFactory loggerFactory,
            IRedisService redisService,
            IMessageQueueService messageQueueService,
            string nodeId,
            NodeInfo nodeInfo,
            LoadBalancingStrategy loadBalancingStrategy = LoadBalancingStrategy.LeastConnections)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _redisService = redisService ?? throw new ArgumentNullException(nameof(redisService));
            _messageQueueService = messageQueueService ?? throw new ArgumentNullException(nameof(messageQueueService));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _nodeInfo = nodeInfo ?? throw new ArgumentNullException(nameof(nodeInfo));
            
            _knownNodes = new ConcurrentDictionary<string, NodeInfo>();
            _cancellationTokenSource = new CancellationTokenSource();

            // Create specific loggers using logger factory / 使用 logger factory 创建特定类型的 logger
            var discoveryLogger = loggerFactory.CreateLogger<RedisNodeDiscoveryService>();
            var loadBalancerLogger = loggerFactory.CreateLogger<LoadBalancer>();

            _discoveryService = new RedisNodeDiscoveryService(
                discoveryLogger,
                redisService,
                nodeId,
                nodeInfo);

            _loadBalancer = new LoadBalancer(loadBalancerLogger, loadBalancingStrategy);

            // Subscribe to discovery events / 订阅发现事件
            _discoveryService.NodeDiscovered += OnNodeDiscovered;
            _discoveryService.NodeRemoved += OnNodeRemoved;
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

            _logger.LogInformation($"Starting hybrid cluster transport for node {_nodeId}");

            try
            {
                // Connect to Redis / 连接到 Redis
                await _redisService.ConnectAsync();

                // Start discovery service / 启动发现服务
                await _discoveryService.StartAsync();

                // Connect to message queue / 连接到消息队列
                await _messageQueueService.ConnectAsync();

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
                await _messageQueueService.ConsumeAsync(_queueName, HandleMessageAsync, autoAck: false);

                _started = true;
                _logger.LogInformation($"Hybrid cluster transport started for node {_nodeId}");
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

                var routingKey = $"node.{nodeId}";
                var messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

                var properties = new MessageProperties
                {
                    MessageId = message.MessageId,
                    CorrelationId = message.MessageId,
                    Timestamp = message.Timestamp
                };

                await _messageQueueService.PublishAsync(ExchangeName, routingKey, messageBytes, properties);

                _logger.LogDebug($"Sent message {message.MessageId} to node {nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send message to node {nodeId}");
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

                var messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

                var properties = new MessageProperties
                {
                    MessageId = message.MessageId,
                    CorrelationId = message.MessageId,
                    Timestamp = message.Timestamp
                };

                await _messageQueueService.PublishAsync(ExchangeName, BroadcastRoutingKey, messageBytes, properties);

                _logger.LogDebug($"Broadcasted message {message.MessageId} to all nodes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast message");
                throw;
            }
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
                return false;
            }

            if (nodeId == _nodeId)
            {
                return true; // Current node is always "connected" / 当前节点始终"已连接"
            }

            return _knownNodes.TryGetValue(nodeId, out var nodeInfo) && 
                   nodeInfo.Status == NodeStatus.Active &&
                   DateTime.UtcNow - nodeInfo.LastHeartbeat < TimeSpan.FromSeconds(60);
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
        /// Handle incoming message / 处理传入消息
        /// </summary>
        private async Task<bool> HandleMessageAsync(byte[] body, MessageProperties properties)
        {
            try
            {
                var messageJson = Encoding.UTF8.GetString(body);
                var message = JsonSerializer.Deserialize<ClusterMessage>(messageJson);

                if (message == null)
                {
                    _logger.LogWarning("Received null or invalid message");
                    return true; // Ack to remove from queue / 确认以从队列中移除
                }

                // Ignore messages from self / 忽略来自自己的消息
                if (message.FromNodeId == _nodeId)
                {
                    return true;
                }

                // Trigger message received event / 触发消息接收事件
                MessageReceived?.Invoke(this, new ClusterMessageEventArgs
                {
                    FromNodeId = message.FromNodeId,
                    Message = message
                });

                // Acknowledge message / 确认消息
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

        /// <summary>
        /// Handle node discovered event / 处理节点发现事件
        /// </summary>
        private void OnNodeDiscovered(object sender, NodeInfo nodeInfo)
        {
            if (nodeInfo == null || nodeInfo.NodeId == _nodeId)
            {
                return;
            }

            var wasNew = _knownNodes.TryAdd(nodeInfo.NodeId, nodeInfo);
            if (wasNew)
            {
                _logger.LogInformation($"Node {nodeInfo.NodeId} discovered: {nodeInfo.Address}:{nodeInfo.Port}");
                NodeConnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeInfo.NodeId });
            }
            else
            {
                // Update existing node info / 更新现有节点信息
                _knownNodes[nodeInfo.NodeId] = nodeInfo;
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
                _logger.LogInformation($"Node {nodeId} removed from cluster");
                NodeDisconnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });
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

            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during disposal");
            }

            _discoveryService?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
}

