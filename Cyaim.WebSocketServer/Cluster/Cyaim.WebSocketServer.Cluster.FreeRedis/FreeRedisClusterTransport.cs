using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        /// Node liveness tracker based on last-seen message timestamps (Raft heartbeats arrive ~every second)
        /// 基于最后一次收到消息时间戳的节点存活跟踪器（Raft 心跳约每秒到达一次）
        /// </summary>
        private readonly NodeLivenessTracker _liveness;

        /// <summary>
        /// Redis client / Redis 客户端
        /// </summary>
        private RedisClient _redis;

        /// <summary>
        /// Active pub/sub subscriptions (disposed and recreated on reconnect)
        /// 活跃的发布/订阅（在重连时释放并重建）
        /// </summary>
        private IDisposable _nodeSubscription;
        private IDisposable _broadcastSubscription;

        /// <summary>
        /// Watchdog timer that verifies the Redis connection and resubscribes after an outage
        /// 校验 Redis 连接并在故障恢复后重新订阅的看门狗定时器
        /// </summary>
        private Timer _connectionWatchdogTimer;

        /// <summary>
        /// Whether the last watchdog ping succeeded / 上一次看门狗 ping 是否成功
        /// </summary>
        private volatile bool _connectionHealthy = true;

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

            // Node liveness: consider a node disconnected when no message (incl. Raft heartbeat ~1s)
            // has been seen within the timeout window (default 15s, checked every 5s)
            // 节点存活：当在超时窗口（默认 15 秒，每 5 秒检查一次）内未收到任何消息
            // （包括约每秒一次的 Raft 心跳）时，认为节点已断开
            _liveness = new NodeLivenessTracker();
            _liveness.NodeTimedOut += (sender, e) =>
            {
                _logger.LogWarning($"Node {e.NodeId} has not been seen within the liveness timeout, raising NodeDisconnected");
                NodeDisconnected?.Invoke(this, e);
            };
            _liveness.NodeRecovered += (sender, e) =>
            {
                _logger.LogInformation($"Node {e.NodeId} is reachable again, raising NodeConnected");
                NodeConnected?.Invoke(this, e);
            };
        }

        /// <summary>
        /// Handle a cluster message received from FreeRedis / 处理从 FreeRedis 接收到的集群消息
        /// </summary>
        /// <param name="messageJson">Serialized cluster message / 序列化的集群消息</param>
        /// <param name="isBroadcast">Whether the message came from the broadcast channel / 消息是否来自广播通道</param>
        private void HandleRedisMessage(string messageJson, bool isBroadcast)
        {
            try
            {
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

                // Skip messages from self on the broadcast channel / 跳过广播通道上来自自己的消息
                if (isBroadcast && clusterMessage.FromNodeId == _nodeId)
                {
                    return;
                }

                // Update node liveness on every received message / 每收到一条消息更新节点存活状态
                if (!string.IsNullOrEmpty(clusterMessage.FromNodeId) && clusterMessage.FromNodeId != _nodeId)
                {
                    _liveness.Touch(clusterMessage.FromNodeId);
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
        }

        /// <summary>
        /// (Re)create the pub/sub subscriptions / （重新）创建发布/订阅
        /// </summary>
        private void SubscribeChannels()
        {
            // Dispose previous subscriptions before recreating them to avoid duplicates
            // 在重建之前释放旧的订阅，避免重复订阅
            try { _nodeSubscription?.Dispose(); } catch { /* ignore / 忽略 */ }
            try { _broadcastSubscription?.Dispose(); } catch { /* ignore / 忽略 */ }

            // Subscribe to node-specific channel / 订阅节点特定通道
            _nodeSubscription = _redis.Subscribe($"{ClusterNodeChannelPrefix}{_nodeId}", (channel, message) =>
            {
                HandleRedisMessage(message?.ToString(), isBroadcast: false);
            });

            // Subscribe to broadcast channel / 订阅广播通道
            _broadcastSubscription = _redis.Subscribe(ClusterBroadcastChannel, (channel, message) =>
            {
                HandleRedisMessage(message?.ToString(), isBroadcast: true);
            });
        }

        /// <summary>
        /// Watchdog that pings Redis and resubscribes after the connection recovers from an outage
        /// 看门狗：ping Redis，并在连接从故障中恢复后重新订阅
        /// </summary>
        /// <param name="state">Timer state / 定时器状态</param>
        private void CheckConnection(object state)
        {
            if (_disposed || _cancellationTokenSource.Token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                _redis?.Ping();

                if (!_connectionHealthy)
                {
                    // Connection recovered: recreate subscriptions to make sure pub/sub is live again
                    // 连接已恢复：重建订阅以确保发布/订阅重新生效
                    _logger.LogInformation($"FreeRedis connection recovered for node {_nodeId}, re-subscribing cluster channels");
                    SubscribeChannels();
                    _connectionHealthy = true;
                }
            }
            catch (Exception ex)
            {
                if (_connectionHealthy)
                {
                    _connectionHealthy = false;
                    _logger.LogWarning(ex, $"FreeRedis connection unhealthy for node {_nodeId}, will re-subscribe once the connection recovers");
                }
            }
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
                _redis.Unavailable += (sender, e) =>
                {
                    _logger.LogWarning($"FreeRedis connection unavailable (host: {e.Host}), waiting for recovery");
                };

                SubscribeChannels();

                // Start connection watchdog to resubscribe after connection drops
                // 启动连接看门狗，在连接断开恢复后重新订阅
                _connectionWatchdogTimer = new Timer(CheckConnection, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

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
                _connectionWatchdogTimer?.Dispose();
                _connectionWatchdogTimer = null;

                try { _nodeSubscription?.Dispose(); } catch { /* ignore / 忽略 */ }
                try { _broadcastSubscription?.Dispose(); } catch { /* ignore / 忽略 */ }
                _nodeSubscription = null;
                _broadcastSubscription = null;

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
            // Liveness-based check: a node is connected if a message from it (incl. Raft heartbeats)
            // was seen within the liveness timeout.
            // If the node has never been seen (e.g. right after startup, before the first heartbeat),
            // fall back to configuration presence to avoid false negatives during the startup grace period.
            // 基于存活状态的检查：如果在存活超时时间内观察到来自该节点的消息（包括 Raft 心跳），则认为节点已连接。
            // 如果从未观察到该节点（例如刚启动、尚未收到第一个心跳），则回退到配置存在性检查，
            // 以避免启动宽限期内的误报。
            if (_liveness.HasBeenSeen(nodeId))
            {
                return _liveness.IsAlive(nodeId);
            }

            return _nodes.ContainsKey(nodeId);
        }

        /// <summary>
        /// Store connection route information (not supported by FreeRedis Pub/Sub transport)
        /// 存储连接路由信息（FreeRedis Pub/Sub 传输不支持）
        /// </summary>
        public Task<bool> StoreConnectionRouteAsync(string connectionId, string nodeId, Dictionary<string, string> metadata = null)
        {
            // FreeRedis Pub/Sub transport doesn't support connection route storage
            // FreeRedis Pub/Sub 传输不支持连接路由存储
            return Task.FromResult(false);
        }

        /// <summary>
        /// Get connection route information (not supported by FreeRedis Pub/Sub transport)
        /// 获取连接路由信息（FreeRedis Pub/Sub 传输不支持）
        /// </summary>
        public Task<string> GetConnectionRouteAsync(string connectionId)
        {
            // FreeRedis Pub/Sub transport doesn't support connection route query
            // FreeRedis Pub/Sub 传输不支持连接路由查询
            return Task.FromResult<string>(null);
        }

        /// <summary>
        /// Remove connection route information (not supported by FreeRedis Pub/Sub transport)
        /// 删除连接路由信息（FreeRedis Pub/Sub 传输不支持）
        /// </summary>
        public Task<bool> RemoveConnectionRouteAsync(string connectionId)
        {
            // FreeRedis Pub/Sub transport doesn't support connection route removal
            // FreeRedis Pub/Sub 传输不支持连接路由删除
            return Task.FromResult(false);
        }

        /// <summary>
        /// Refresh connection route expiration (not supported by FreeRedis Pub/Sub transport)
        /// 刷新连接路由过期时间（FreeRedis Pub/Sub 传输不支持）
        /// </summary>
        public Task<bool> RefreshConnectionRouteAsync(string connectionId, string nodeId)
        {
            // FreeRedis Pub/Sub transport doesn't support connection route refresh
            // FreeRedis Pub/Sub 传输不支持连接路由刷新
            return Task.FromResult(false);
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
                _liveness.Dispose();
                _cancellationTokenSource.Dispose();
                _disposed = true;
            }
        }
    }
}

