using System;
using System.Collections.Concurrent;
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
        /// RabbitMQ connection / RabbitMQ 连接
        /// </summary>
        private IConnection _connection;
        /// <summary>
        /// RabbitMQ channel / RabbitMQ 通道
        /// </summary>
        private IModel _channel;
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
        /// Event triggered when node disconnected / 节点断开连接时触发的事件
        /// </summary>
#pragma warning disable CS0067 // 事件从未使用，但可能是接口要求的一部分
        public event EventHandler<ClusterNodeEventArgs> NodeDisconnected;
#pragma warning restore CS0067

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
        }

        /// <summary>
        /// Start the transport service / 启动传输服务
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogInformation($"Starting RabbitMQ cluster transport for node {_nodeId}");
            
            try
            {
                var factory = new ConnectionFactory { Uri = new Uri(_connectionString) };
                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();
                
                // Declare exchange / 声明交换机
                _channel.ExchangeDeclare(ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false);
                
                // Declare queue for this node / 为此节点声明队列
                _queueName = _channel.QueueDeclare(
                    queue: $"cluster:node:{_nodeId}",
                    durable: false,
                    exclusive: false,
                    autoDelete: true,
                    arguments: null).QueueName;
                
                // Bind queue to node-specific routing key / 将队列绑定到节点特定的路由键
                _channel.QueueBind(_queueName, ExchangeName, _nodeId);
                
                // Also bind to broadcast / 同时绑定到广播
                _channel.QueueBind(_queueName, ExchangeName, BroadcastRoutingKey);
                
                // Start consuming / 开始消费
                var consumer = new EventingBasicConsumer(_channel);
                consumer.Received += (model, ea) =>
                {
                    try
                    {
                        var body = ea.Body.ToArray();
                        var messageJson = Encoding.UTF8.GetString(body);
                        var clusterMessage = JsonSerializer.Deserialize<ClusterMessage>(messageJson);
                        
                        // Skip messages from self / 跳过来自自己的消息
                        if (clusterMessage.FromNodeId == _nodeId)
                        {
                            _channel.BasicAck(ea.DeliveryTag, false);
                            return;
                        }
                        
                        MessageReceived?.Invoke(this, new ClusterMessageEventArgs
                        {
                            FromNodeId = clusterMessage.FromNodeId,
                            Message = clusterMessage
                        });
                        
                        _channel.BasicAck(ea.DeliveryTag, false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process RabbitMQ message");
                        _channel.BasicNack(ea.DeliveryTag, false, true);
                    }
                };
                
                _channel.BasicConsume(
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
                _channel?.Close();
                _channel?.Dispose();
                _connection?.Close();
                _connection?.Dispose();
                
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
                if (_channel == null || !_channel.IsOpen)
                {
                    _logger.LogWarning("RabbitMQ channel is not available");
                    return;
                }
                
                var properties = _channel.CreateBasicProperties();
                properties.Persistent = false;
                properties.MessageId = message.MessageId;
                properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                
                _channel.BasicPublish(
                    exchange: ExchangeName,
                    routingKey: nodeId,
                    basicProperties: properties,
                    body: messageBytes);
                
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
                if (_channel == null || !_channel.IsOpen)
                {
                    _logger.LogWarning("RabbitMQ channel is not available");
                    return;
                }
                
                var properties = _channel.CreateBasicProperties();
                properties.Persistent = false;
                properties.MessageId = message.MessageId;
                properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                
                _channel.BasicPublish(
                    exchange: ExchangeName,
                    routingKey: BroadcastRoutingKey,
                    basicProperties: properties,
                    body: messageBytes);
                
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
            // With RabbitMQ, we consider a node connected if it's registered
            // 对于 RabbitMQ，如果节点已注册，我们认为它已连接
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
                _cancellationTokenSource.Dispose();
                _disposed = true;
            }
        }
    }
}

