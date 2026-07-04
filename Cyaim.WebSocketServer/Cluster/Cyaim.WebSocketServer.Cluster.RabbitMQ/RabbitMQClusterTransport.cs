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
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Cyaim.WebSocketServer.Cluster.RabbitMQ
{
    /// <summary>
    /// RabbitMQ-based cluster transport
    /// 基于 RabbitMQ 的集群传输
    /// Supports RabbitMQ.Client 7.0+ (uses AsyncEventingBasicConsumer)
    /// 支持 RabbitMQ.Client 7.0+（使用 AsyncEventingBasicConsumer）
    /// </summary>
    public class RabbitMQClusterTransport : IClusterTransport
    {
        private readonly ILogger<RabbitMQClusterTransport> _logger;
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
        /// Idle expiration for the node queue in milliseconds (x-expires).
        /// The queue survives brief consumer disconnects (no message loss), while abandoned
        /// queues are still cleaned up by the broker after this period of inactivity.
        /// 节点队列的空闲过期时间（毫秒，x-expires）。
        /// 队列可以在消费者短暂断开时保留（不丢消息），而废弃的队列在此不活跃时长后仍会被代理清理。
        /// </summary>
        private const int QueueExpiresMilliseconds = 5 * 60 * 1000;

        /// <summary>
        /// RabbitMQ connection / RabbitMQ 连接
        /// </summary>
        private IConnection _connection;
        /// <summary>
        /// RabbitMQ channel / RabbitMQ 通道
        /// RabbitMQ.Client 7.0+ uses IChannel instead of IModel
        /// RabbitMQ.Client 7.0+ 使用 IChannel 替代 IModel
        /// </summary>
        private IChannel _channel;
        /// <summary>
        /// Queue name for this node / 此节点的队列名称
        /// </summary>
        private string _queueName;
        /// <summary>
        /// Exchange name / 交换机名称
        /// </summary>
        private const string ExchangeName = "cluster_exchange";
        /// <summary>
        /// Broadcast routing key / 广播路由键
        /// </summary>
        private const string BroadcastRoutingKey = "broadcast";

        /// <summary>
        /// Event triggered when message received / 消息接收时触发的事件
        /// </summary>
        public event EventHandler<ClusterMessageEventArgs> MessageReceived;
        /// <summary>
        /// Event triggered when node connected / 节点连接时触发的事件
        /// </summary>
        public event EventHandler<ClusterNodeEventArgs> NodeConnected;
        /// <summary>
        /// Event triggered when node disconnected (raised when a node has not been seen within the liveness timeout)
        /// 节点断开连接时触发的事件（当节点在存活超时时间内未被观察到时触发）
        /// </summary>
        public event EventHandler<ClusterNodeEventArgs> NodeDisconnected;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <param name="connectionString">RabbitMQ connection string / RabbitMQ 连接字符串</param>
        public RabbitMQClusterTransport(ILogger<RabbitMQClusterTransport> logger, string nodeId, string connectionString)
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
        /// Start the transport service / 启动传输服务
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogInformation($"Starting RabbitMQ cluster transport for node {_nodeId}");

            try
            {
                // Enable automatic connection and topology recovery so the consumer, queue and
                // bindings are restored after a broker/network outage without manual handling
                // 启用自动连接和拓扑恢复，使消费者、队列和绑定在代理/网络故障后自动恢复，无需手动处理
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(_connectionString),
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
                };
                _connection = await factory.CreateConnectionAsync();
                _connection.ConnectionShutdownAsync += (sender, e) =>
                {
                    _logger.LogWarning($"RabbitMQ connection shutdown (reason: {e.ReplyText}), automatic recovery will reconnect");
                    return Task.CompletedTask;
                };
                _connection.RecoverySucceededAsync += (sender, e) =>
                {
                    _logger.LogInformation("RabbitMQ connection recovery succeeded, consumer and topology restored");
                    return Task.CompletedTask;
                };

                _channel = await _connection.CreateChannelAsync();

                // Declare exchange / 声明交换机
                await _channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false);

                // Declare queue for this node / 为此节点声明队列
                // autoDelete must be false: an auto-delete queue is removed the moment the consumer
                // briefly disconnects, losing all queued messages. Instead, use x-expires so the
                // broker cleans up the queue only after it has been unused for a while.
                // autoDelete 必须为 false：自动删除队列会在消费者短暂断开的瞬间被删除，丢失所有排队的消息。
                // 改用 x-expires，让代理仅在队列长时间未被使用后才清理它。
                var queueDeclareResult = await _channel.QueueDeclareAsync(
                    queue: $"cluster:node:{_nodeId}",
                    durable: false,
                    exclusive: false,
                    autoDelete: false,
                    arguments: new Dictionary<string, object>
                    {
                        ["x-expires"] = QueueExpiresMilliseconds
                    });
                _queueName = queueDeclareResult.QueueName;
                
                // Bind queue to node-specific routing key / 将队列绑定到节点特定的路由键
                await _channel.QueueBindAsync(_queueName, ExchangeName, _nodeId);
                
                // Also bind to broadcast / 同时绑定到广播
                await _channel.QueueBindAsync(_queueName, ExchangeName, BroadcastRoutingKey);
                
                // Start consuming / 开始消费
                // Use AsyncEventingBasicConsumer for RabbitMQ.Client 7.0+
                // 使用 AsyncEventingBasicConsumer 支持 RabbitMQ.Client 7.0+
                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += async (model, ea) =>
                {
                    try
                    {
                        var body = ea.Body.ToArray();
                        var messageJson = Encoding.UTF8.GetString(body);
                        var clusterMessage = JsonSerializer.Deserialize<ClusterMessage>(messageJson);
                        
                        if (clusterMessage == null)
                        {
                            _logger.LogWarning("Received null or invalid message from RabbitMQ");
                            await _channel.BasicAckAsync(ea.DeliveryTag, false);
                            return;
                        }
                        
                        // Skip messages from self / 跳过来自自己的消息
                        if (clusterMessage.FromNodeId == _nodeId)
                        {
                            await _channel.BasicAckAsync(ea.DeliveryTag, false);
                            return;
                        }

                        // Update node liveness on every received message / 每收到一条消息更新节点存活状态
                        if (!string.IsNullOrEmpty(clusterMessage.FromNodeId))
                        {
                            _liveness.Touch(clusterMessage.FromNodeId);
                        }

                        MessageReceived?.Invoke(this, new ClusterMessageEventArgs
                        {
                            FromNodeId = clusterMessage.FromNodeId,
                            Message = clusterMessage
                        });
                        
                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process RabbitMQ message");
                        await _channel.BasicNackAsync(ea.DeliveryTag, false, true);
                    }
                };
                
                await _channel.BasicConsumeAsync(
                    queue: _queueName,
                    autoAck: false,
                    consumer: consumer);

                _logger.LogInformation($"RabbitMQ cluster transport started successfully for node {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start RabbitMQ cluster transport");
                throw;
            }
            
            await Task.CompletedTask;
        }

        /// <summary>
        /// Stop the transport service / 停止传输服务
        /// </summary>
        public async Task StopAsync()
        {
            _logger.LogInformation($"Stopping RabbitMQ cluster transport for node {_nodeId}");
            _cancellationTokenSource.Cancel();
            
            try
            {
                if (_channel != null)
                {
                    await _channel.CloseAsync();
                    _channel.Dispose();
                    _channel = null;
                }
                if (_connection != null)
                {
                    await _connection.CloseAsync();
                    _connection.Dispose();
                    _connection = null;
                }
                
                _nodes.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping RabbitMQ cluster transport");
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
            var messageBytes = Encoding.UTF8.GetBytes(messageJson);
            
            try
            {
                if (_channel == null)
                {
                    _logger.LogWarning("RabbitMQ channel is not available");
                    return;
                }
                
                var properties = new BasicProperties();
                properties.Persistent = false;
                properties.MessageId = message.MessageId;
                properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                
                await _channel.BasicPublishAsync(
                    exchange: ExchangeName,
                    routingKey: nodeId,
                    mandatory: false,
                    basicProperties: properties,
                    body: new ReadOnlyMemory<byte>(messageBytes));
                
                _logger.LogDebug($"Sent message to node {nodeId} via RabbitMQ");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send message to node {nodeId} via RabbitMQ");
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
            var messageBytes = Encoding.UTF8.GetBytes(messageJson);
            
            try
            {
                if (_channel == null)
                {
                    _logger.LogWarning("RabbitMQ channel is not available");
                    return;
                }
                
                var properties = new BasicProperties();
                properties.Persistent = false;
                properties.MessageId = message.MessageId;
                properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                
                await _channel.BasicPublishAsync(
                    exchange: ExchangeName,
                    routingKey: BroadcastRoutingKey,
                    mandatory: false,
                    basicProperties: properties,
                    body: new ReadOnlyMemory<byte>(messageBytes));
                
                _logger.LogDebug("Broadcasted message via RabbitMQ");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast message via RabbitMQ");
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
        /// Store connection route information (not supported by RabbitMQ transport)
        /// 存储连接路由信息（RabbitMQ 传输不支持）
        /// </summary>
        public Task<bool> StoreConnectionRouteAsync(string connectionId, string nodeId, Dictionary<string, string> metadata = null)
        {
            // RabbitMQ transport doesn't support connection route storage
            // RabbitMQ 传输不支持连接路由存储
            return Task.FromResult(false);
        }

        /// <summary>
        /// Get connection route information (not supported by RabbitMQ transport)
        /// 获取连接路由信息（RabbitMQ 传输不支持）
        /// </summary>
        public Task<string> GetConnectionRouteAsync(string connectionId)
        {
            // RabbitMQ transport doesn't support connection route query
            // RabbitMQ 传输不支持连接路由查询
            return Task.FromResult<string>(null);
        }

        /// <summary>
        /// Remove connection route information (not supported by RabbitMQ transport)
        /// 删除连接路由信息（RabbitMQ 传输不支持）
        /// </summary>
        public Task<bool> RemoveConnectionRouteAsync(string connectionId)
        {
            // RabbitMQ transport doesn't support connection route removal
            // RabbitMQ 传输不支持连接路由删除
            return Task.FromResult(false);
        }

        /// <summary>
        /// Refresh connection route expiration (not supported by RabbitMQ transport)
        /// 刷新连接路由过期时间（RabbitMQ 传输不支持）
        /// </summary>
        public Task<bool> RefreshConnectionRouteAsync(string connectionId, string nodeId)
        {
            // RabbitMQ transport doesn't support connection route refresh
            // RabbitMQ 传输不支持连接路由刷新
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
            _logger.LogDebug($"Registered node {node.NodeId} in RabbitMQ transport");
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

