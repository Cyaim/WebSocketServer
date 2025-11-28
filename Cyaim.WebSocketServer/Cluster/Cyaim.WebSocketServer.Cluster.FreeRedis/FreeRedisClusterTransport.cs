using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging;
using FreeRedis;
using ClusterNode = Cyaim.WebSocketServer.Infrastructure.Cluster.ClusterNode;

namespace Cyaim.WebSocketServer.Cluster.FreeRedis
{
    /// <summary>
    /// FreeRedis-based cluster transport (using Redis Pub/Sub)
    /// 基于 FreeRedis 的集群传输（使用 Redis 发布/订阅）
    /// </summary>
    public class FreeRedisClusterTransport : IClusterTransport
    {
        /// <summary>
        /// Redis channel prefix for node-specific messages / Redis 节点特定消息的通道前缀
        /// </summary>
        private const string ClusterNodeChannelPrefix = "cluster:node:";

        /// <summary>
        /// Redis channel name for broadcast messages / Redis 广播消息的通道名称
        /// </summary>
        private const string ClusterBroadcastChannel = "cluster:broadcast";

        private readonly ILogger<FreeRedisClusterTransport> _logger;
        private readonly string _nodeId;
        private readonly string _connectionString;
        private readonly ConcurrentDictionary<string, ClusterNode> _nodes;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _disposed = false;

        /// <summary>
        /// Redis client / Redis 客户端
        /// </summary>
        private RedisClient _redis;

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
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <param name="connectionString">Redis connection string / Redis 连接字符串</param>
        public FreeRedisClusterTransport(ILogger<FreeRedisClusterTransport> logger, string nodeId, string connectionString)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _nodes = new ConcurrentDictionary<string, ClusterNode>();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Start the transport service / 启动传输服务
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogInformation($"Starting FreeRedis cluster transport for node {_nodeId}");

            try
            {
                _redis = new RedisClient(_connectionString);

                // Subscribe to node-specific channel / 订阅节点特定通道
                _redis.Subscribe($"{ClusterNodeChannelPrefix}{_nodeId}", (channel, message) =>
                {
                    try
                    {
                        var messageJson = message?.ToString();
                        if (string.IsNullOrEmpty(messageJson))
                        {
                            _logger.LogWarning("Received empty message from FreeRedis");
                            return;
                        }

                        var clusterMessage = JsonSerializer.Deserialize<ClusterMessage>(messageJson);
                        if (clusterMessage == null)
                        {
                            _logger.LogWarning("Failed to deserialize cluster message from FreeRedis");
                            return;
                        }

                        MessageReceived?.Invoke(this, new ClusterMessageEventArgs
                        {
                            FromNodeId = clusterMessage.FromNodeId,
                            Message = clusterMessage
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process FreeRedis message");
                    }
                });

                // Subscribe to broadcast channel / 订阅广播通道
                _redis.Subscribe(ClusterBroadcastChannel, (channel, message) =>
                {
                    try
                    {
                        var messageJson = message?.ToString();
                        if (string.IsNullOrEmpty(messageJson))
                        {
                            _logger.LogWarning("Received empty broadcast message from FreeRedis");
                            return;
                        }

                        var clusterMessage = JsonSerializer.Deserialize<ClusterMessage>(messageJson);
                        if (clusterMessage == null)
                        {
                            _logger.LogWarning("Failed to deserialize cluster broadcast message from FreeRedis");
                            return;
                        }

                        if (clusterMessage.FromNodeId != _nodeId)
                        {
                            MessageReceived?.Invoke(this, new ClusterMessageEventArgs
                            {
                                FromNodeId = clusterMessage.FromNodeId,
                                Message = clusterMessage
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process FreeRedis broadcast message");
                    }
                });

                _logger.LogInformation($"FreeRedis cluster transport started successfully for node {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start FreeRedis cluster transport");
                throw;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Stop the transport service / 停止传输服务
        /// </summary>
        public async Task StopAsync()
        {
            _logger.LogInformation($"Stopping FreeRedis cluster transport for node {_nodeId}");
            _cancellationTokenSource.Cancel();

            try
            {
                if (_redis != null)
                {
                    _redis.UnSubscribe($"{ClusterNodeChannelPrefix}{_nodeId}");
                    _redis.UnSubscribe(ClusterBroadcastChannel);
                    _redis.Dispose();
                    _redis = null;
                }

                _nodes.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping FreeRedis cluster transport");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Send message to specific node / 向指定节点发送消息
        /// </summary>
        /// <param name="nodeId">Target node ID / 目标节点 ID</param>
        /// <param name="message">Message to send / 要发送的消息</param>
        public async Task SendAsync(string nodeId, ClusterMessage message)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            message.FromNodeId = _nodeId;
            message.ToNodeId = nodeId;
            var messageJson = JsonSerializer.Serialize(message);

            try
            {
                if (_redis == null)
                {
                    _logger.LogWarning("FreeRedis client is not initialized");
                    return;
                }

                _redis.Publish($"{ClusterNodeChannelPrefix}{nodeId}", messageJson);

                _logger.LogDebug($"Sent message to node {nodeId} via FreeRedis");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send message to node {nodeId} via FreeRedis");
                throw;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Broadcast message to all nodes / 向所有节点广播消息
        /// </summary>
        /// <param name="message">Message to broadcast / 要广播的消息</param>
        public async Task BroadcastAsync(ClusterMessage message)
        {
            message.FromNodeId = _nodeId;
            var messageJson = JsonSerializer.Serialize(message);

            try
            {
                if (_redis == null)
                {
                    _logger.LogWarning("FreeRedis client is not initialized");
                    return;
                }

                _redis.Publish(ClusterBroadcastChannel, messageJson);

                _logger.LogDebug("Broadcasted message via FreeRedis");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast message via FreeRedis");
                throw;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Check if node is connected / 检查节点是否已连接
        /// </summary>
        /// <param name="nodeId">Node ID to check / 要检查的节点 ID</param>
        /// <returns>True if connected, false otherwise / 已连接返回 true，否则返回 false</returns>
        public bool IsNodeConnected(string nodeId)
        {
            // With FreeRedis, we consider a node connected if it's registered
            // 对于 FreeRedis，如果节点已注册，我们认为它已连接
            // In practice, you might want to track node health separately
            // 在实践中，您可能需要单独跟踪节点健康状况
            return _nodes.ContainsKey(nodeId);
        }

        /// <summary>
        /// Register a node / 注册节点
        /// </summary>
        /// <param name="node">Node information / 节点信息</param>
        public void RegisterNode(ClusterNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            _nodes.AddOrUpdate(node.NodeId, node, (key, oldValue) => node);
            NodeConnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = node.NodeId });
            _logger.LogDebug($"Registered node {node.NodeId} in FreeRedis transport");
        }

        /// <summary>
        /// Dispose resources / 释放资源
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                StopAsync().GetAwaiter().GetResult();
                _cancellationTokenSource.Dispose();
                _disposed = true;
            }
        }
    }
}

