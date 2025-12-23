using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
{
    /// <summary>
    /// RabbitMQ.Client implementation of IMessageQueueService
    /// RabbitMQ.Client 的 IMessageQueueService 实现
    /// Supports RabbitMQ.Client 7.0+ (uses AsyncEventingBasicConsumer)
    /// 支持 RabbitMQ.Client 7.0+（使用 AsyncEventingBasicConsumer）
    /// </summary>
    public class RabbitMQMessageQueueService : IMessageQueueService
    {
        private readonly ILogger<RabbitMQMessageQueueService> _logger;
        private readonly string _connectionString;

        // Connection and Channel / 连接和通道
        private IConnection _connection;
        private IChannel _channel;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private volatile bool _isConnecting = false;

        // Consumer management / 消费者管理
        private readonly Dictionary<string, AsyncEventingBasicConsumer> _consumers = new Dictionary<string, AsyncEventingBasicConsumer>();
        private readonly Dictionary<string, string> _consumerTags = new Dictionary<string, string>();
        private readonly Dictionary<string, ConsumerInfo> _consumerInfos = new Dictionary<string, ConsumerInfo>();
        private readonly SemaphoreSlim _consumerLock = new SemaphoreSlim(1, 1);

        // Resource tracking for reconnection / 资源跟踪以便重连
        private readonly Dictionary<string, ExchangeInfo> _declaredExchanges = new Dictionary<string, ExchangeInfo>();
        private readonly Dictionary<string, QueueInfo> _declaredQueues = new Dictionary<string, QueueInfo>();
        private readonly Dictionary<string, List<BindingInfo>> _queueBindings = new Dictionary<string, List<BindingInfo>>();

        private bool _disposed = false;

        /// <summary>
        /// Consumer information / 消费者信息
        /// </summary>
        private class ConsumerInfo
        {
            public Func<byte[], MessageProperties, Task<bool>> Handler { get; set; }
            public bool AutoAck { get; set; }
            public string CurrentNodeId { get; set; }
        }

        /// <summary>
        /// Exchange information / 交换机信息
        /// </summary>
        private class ExchangeInfo
        {
            public string ExchangeType { get; set; }
            public bool Durable { get; set; }
        }

        /// <summary>
        /// Queue information / 队列信息
        /// </summary>
        private class QueueInfo
        {
            public bool Durable { get; set; }
            public bool Exclusive { get; set; }
            public bool AutoDelete { get; set; }
        }

        /// <summary>
        /// Binding information / 绑定信息
        /// </summary>
        private class BindingInfo
        {
            public string ExchangeName { get; set; }
            public string RoutingKey { get; set; }
        }

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public RabbitMQMessageQueueService(ILogger<RabbitMQMessageQueueService> logger, string connectionString)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        #region Connection Management / 连接管理

        /// <summary>
        /// Connect to message queue / 连接到消息队列
        /// </summary>
        public async Task ConnectAsync()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RabbitMQMessageQueueService));
            }

            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // 如果已连接且正常，直接返回
                if (IsConnected())
                {
                    _logger.LogDebug("[RabbitMQMessageQueueService] 已连接，跳过重连");
                    return;
                }

                // 防止并发连接
                if (_isConnecting)
                {
                    _logger.LogWarning("[RabbitMQMessageQueueService] 正在连接中，等待完成");
                    int waitCount = 0;
                    while (_isConnecting && waitCount < 50) // 最多等待5秒
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        waitCount++;
                        if (IsConnected())
                        {
                            return;
                        }
                    }
                }

                _isConnecting = true;
                try
                {
                    await InternalConnectAsync().ConfigureAwait(false);
                }
                finally
                {
                    _isConnecting = false;
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Internal connection logic / 内部连接逻辑
        /// </summary>
        private async Task InternalConnectAsync()
        {
            try
            {
                // 清理旧连接
                await CleanupConnectionAsync().ConfigureAwait(false);

                _logger.LogInformation("[RabbitMQMessageQueueService] 开始连接 RabbitMQ");
                var factory = new ConnectionFactory { Uri = new Uri(_connectionString) };

                _connection = await factory.CreateConnectionAsync().ConfigureAwait(false);
                _channel = await _connection.CreateChannelAsync().ConfigureAwait(false);

                // 订阅关闭事件
                _connection.ConnectionShutdownAsync += OnConnectionShutdown;
                _channel.ChannelShutdownAsync += OnChannelShutdown;

                _logger.LogInformation("[RabbitMQMessageQueueService] RabbitMQ 连接成功 - ConnectionIsOpen: {ConnectionIsOpen}, ChannelIsOpen: {ChannelIsOpen}",
                    _connection.IsOpen, _channel.IsOpen);

                // 恢复资源（交换机、队列、绑定、消费者）
                await RestoreResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitMQMessageQueueService] RabbitMQ 连接失败");
                await CleanupConnectionAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Cleanup old connection / 清理旧连接
        /// </summary>
        private async Task CleanupConnectionAsync()
        {
            try
            {
                // 取消所有消费者
                await CancelAllConsumersAsync().ConfigureAwait(false);

                // 清理字典
                _consumers.Clear();
                _consumerTags.Clear();

                // 关闭 channel
                if (_channel != null)
                {
                    try
                    {
                        if (_channel.IsOpen)
                        {
                            await _channel.CloseAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[RabbitMQMessageQueueService] 关闭 channel 时发生异常");
                    }
                    finally
                    {
                        _channel?.Dispose();
                        _channel = null;
                    }
                }

                // 关闭 connection
                if (_connection != null)
                {
                    try
                    {
                        if (_connection.IsOpen)
                        {
                            await _connection.CloseAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[RabbitMQMessageQueueService] 关闭 connection 时发生异常");
                    }
                    finally
                    {
                        _connection?.Dispose();
                        _connection = null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitMQMessageQueueService] 清理连接时发生异常");
            }
        }

        /// <summary>
        /// Check if connected / 检查是否已连接
        /// </summary>
        private bool IsConnected()
        {
            try
            {
                return _connection != null && _connection.IsOpen &&
                       _channel != null && _channel.IsOpen;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Ensure connection is ready / 确保连接就绪
        /// </summary>
        private async Task EnsureConnectedAsync()
        {
            if (!IsConnected())
            {
                await ConnectAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Verify connection is ready / 验证连接已就绪
        /// </summary>
        public async Task VerifyConnectionAsync()
        {
            await EnsureConnectedAsync().ConfigureAwait(false);

            if (!IsConnected())
            {
                throw new InvalidOperationException("RabbitMQ connection is not ready");
            }
        }

        /// <summary>
        /// Handle connection shutdown / 处理连接关闭
        /// </summary>
        private async Task OnConnectionShutdown(object sender, ShutdownEventArgs args)
        {
            _logger.LogWarning("[RabbitMQMessageQueueService] 连接关闭事件 - ReplyCode: {ReplyCode}, ReplyText: {ReplyText}",
                args?.ReplyCode, args?.ReplyText);

            // 清理消费者
            _consumers.Clear();
            _consumerTags.Clear();

            // 后台重连
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000).ConfigureAwait(false); // 等待1秒后重连
                    await ConnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RabbitMQMessageQueueService] 连接关闭后重连失败");
                }
            });
        }

        /// <summary>
        /// Handle channel shutdown / 处理通道关闭
        /// </summary>
        private async Task OnChannelShutdown(object sender, ShutdownEventArgs args)
        {
            _logger.LogWarning("[RabbitMQMessageQueueService] 通道关闭事件 - ReplyCode: {ReplyCode}, ReplyText: {ReplyText}",
                args?.ReplyCode, args?.ReplyText);

            // 清理消费者
            _consumers.Clear();
            _consumerTags.Clear();

            // 后台重连
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000).ConfigureAwait(false); // 等待1秒后重连
                    await ConnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RabbitMQMessageQueueService] 通道关闭后重连失败");
                }
            });
        }

        /// <summary>
        /// Disconnect from message queue / 断开消息队列连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await CancelAllConsumersAsync().ConfigureAwait(false);
                await CleanupConnectionAsync().ConfigureAwait(false);
                _logger.LogInformation("[RabbitMQMessageQueueService] 已断开连接");
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        #endregion

        #region Resource Restoration / 资源恢复

        /// <summary>
        /// Restore resources after reconnection / 重连后恢复资源
        /// </summary>
        private async Task RestoreResourcesAsync()
        {
            if (!IsConnected())
            {
                return;
            }

            try
            {
                _logger.LogInformation("[RabbitMQMessageQueueService] 开始恢复资源 - Exchanges: {ExchangeCount}, Queues: {QueueCount}, Consumers: {ConsumerCount}",
                    _declaredExchanges.Count, _declaredQueues.Count, _consumerInfos.Count);

                // 恢复交换机
                foreach (var kvp in _declaredExchanges.ToList())
                {
                    try
                    {
                        await _channel.ExchangeDeclareAsync(kvp.Key, kvp.Value.ExchangeType, durable: kvp.Value.Durable, autoDelete: false)
                            .ConfigureAwait(false);
                        _logger.LogDebug("[RabbitMQMessageQueueService] 恢复交换机成功 - ExchangeName: {ExchangeName}", kvp.Key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[RabbitMQMessageQueueService] 恢复交换机失败 - ExchangeName: {ExchangeName}", kvp.Key);
                    }
                }

                // 恢复队列
                foreach (var kvp in _declaredQueues.ToList())
                {
                    try
                    {
                        await _channel.QueueDeclareAsync(
                            queue: kvp.Key,
                            durable: kvp.Value.Durable,
                            exclusive: kvp.Value.Exclusive,
                            autoDelete: kvp.Value.AutoDelete,
                            arguments: null).ConfigureAwait(false);
                        _logger.LogDebug("[RabbitMQMessageQueueService] 恢复队列成功 - QueueName: {QueueName}", kvp.Key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[RabbitMQMessageQueueService] 恢复队列失败 - QueueName: {QueueName}", kvp.Key);
                    }
                }

                // 恢复绑定
                foreach (var kvp in _queueBindings.ToList())
                {
                    foreach (var binding in kvp.Value.ToList())
                    {
                        try
                        {
                            await _channel.QueueBindAsync(kvp.Key, binding.ExchangeName, binding.RoutingKey)
                                .ConfigureAwait(false);
                            _logger.LogDebug("[RabbitMQMessageQueueService] 恢复绑定成功 - QueueName: {QueueName}, ExchangeName: {ExchangeName}, RoutingKey: {RoutingKey}",
                                kvp.Key, binding.ExchangeName, binding.RoutingKey);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[RabbitMQMessageQueueService] 恢复绑定失败 - QueueName: {QueueName}, ExchangeName: {ExchangeName}, RoutingKey: {RoutingKey}",
                                kvp.Key, binding.ExchangeName, binding.RoutingKey);
                        }
                    }
                }

                // 恢复消费者
                await RestoreConsumersAsync().ConfigureAwait(false);

                _logger.LogInformation("[RabbitMQMessageQueueService] 资源恢复完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitMQMessageQueueService] 恢复资源时发生异常");
            }
        }

        /// <summary>
        /// Restore consumers after reconnection / 重连后恢复消费者
        /// </summary>
        private async Task RestoreConsumersAsync()
        {
            if (!IsConnected())
            {
                return;
            }

            await _consumerLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var queueNames = _consumerInfos.Keys.ToList();
                _logger.LogInformation("[RabbitMQMessageQueueService] 开始恢复消费者 - Count: {Count}", queueNames.Count);

                foreach (var queueName in queueNames)
                {
                    try
                    {
                        var info = _consumerInfos[queueName];
                        await InternalConsumeAsync(queueName, info.Handler, info.AutoAck, info.CurrentNodeId, skipCheck: true)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RabbitMQMessageQueueService] 恢复消费者失败 - QueueName: {QueueName}", queueName);
                    }
                }

                _logger.LogInformation("[RabbitMQMessageQueueService] 消费者恢复完成 - SuccessCount: {SuccessCount}", _consumers.Count);
            }
            finally
            {
                _consumerLock.Release();
            }
        }

        /// <summary>
        /// Cancel all consumers / 取消所有消费者
        /// </summary>
        private async Task CancelAllConsumersAsync()
        {
            if (_channel == null || !_channel.IsOpen)
            {
                return;
            }

            var tagsToCancel = _consumerTags.Values.Where(t => !string.IsNullOrEmpty(t)).ToList();
            foreach (var tag in tagsToCancel)
            {
                try
                {
                    await _channel.BasicCancelAsync(tag).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略取消失败的情况
                }
            }
        }

        #endregion

        #region Exchange and Queue Management / 交换机和队列管理

        /// <summary>
        /// Declare an exchange / 声明交换机
        /// </summary>
        public async Task DeclareExchangeAsync(string exchangeName, string exchangeType, bool durable = true)
        {
            if (string.IsNullOrEmpty(exchangeName))
            {
                throw new ArgumentException("Exchange name cannot be null or empty", nameof(exchangeName));
            }
            if (string.IsNullOrEmpty(exchangeType))
            {
                throw new ArgumentException("Exchange type cannot be null or empty", nameof(exchangeType));
            }

            await EnsureConnectedAsync().ConfigureAwait(false);

            try
            {
                await _channel.ExchangeDeclareAsync(exchangeName, exchangeType, durable: durable, autoDelete: false)
                    .ConfigureAwait(false);

                // 保存信息以便重连时恢复
                _declaredExchanges[exchangeName] = new ExchangeInfo
                {
                    ExchangeType = exchangeType,
                    Durable = durable
                };

                _logger.LogDebug("[RabbitMQMessageQueueService] 声明交换机成功 - ExchangeName: {ExchangeName}, ExchangeType: {ExchangeType}",
                    exchangeName, exchangeType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitMQMessageQueueService] 声明交换机失败 - ExchangeName: {ExchangeName}", exchangeName);
                throw;
            }
        }

        /// <summary>
        /// Declare a queue / 声明队列
        /// </summary>
        public async Task<string> DeclareQueueAsync(string queueName, bool durable = false, bool exclusive = false, bool autoDelete = false)
        {
            if (string.IsNullOrEmpty(queueName))
            {
                throw new ArgumentException("Queue name cannot be null or empty", nameof(queueName));
            }

            await EnsureConnectedAsync().ConfigureAwait(false);

            try
            {
                var result = await _channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: durable,
                    exclusive: exclusive,
                    autoDelete: autoDelete,
                    arguments: null).ConfigureAwait(false);

                // 保存信息以便重连时恢复
                _declaredQueues[queueName] = new QueueInfo
                {
                    Durable = durable,
                    Exclusive = exclusive,
                    AutoDelete = autoDelete
                };

                _logger.LogDebug("[RabbitMQMessageQueueService] 声明队列成功 - QueueName: {QueueName}, ConsumerCount: {ConsumerCount}, MessageCount: {MessageCount}",
                    result.QueueName, result.ConsumerCount, result.MessageCount);

                return result.QueueName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitMQMessageQueueService] 声明队列失败 - QueueName: {QueueName}", queueName);
                throw;
            }
        }

        /// <summary>
        /// Bind queue to exchange / 将队列绑定到交换机
        /// </summary>
        public async Task BindQueueAsync(string queueName, string exchangeName, string routingKey)
        {
            if (string.IsNullOrEmpty(queueName))
            {
                throw new ArgumentException("Queue name cannot be null or empty", nameof(queueName));
            }
            if (string.IsNullOrEmpty(exchangeName))
            {
                throw new ArgumentException("Exchange name cannot be null or empty", nameof(exchangeName));
            }
            if (string.IsNullOrEmpty(routingKey))
            {
                throw new ArgumentException("Routing key cannot be null or empty", nameof(routingKey));
            }

            await EnsureConnectedAsync().ConfigureAwait(false);

            try
            {
                await _channel.QueueBindAsync(queueName, exchangeName, routingKey).ConfigureAwait(false);

                // 保存绑定信息以便重连时恢复
                if (!_queueBindings.ContainsKey(queueName))
                {
                    _queueBindings[queueName] = new List<BindingInfo>();
                }

                // 检查是否已存在
                if (!_queueBindings[queueName].Any(b => b.ExchangeName == exchangeName && b.RoutingKey == routingKey))
                {
                    _queueBindings[queueName].Add(new BindingInfo
                    {
                        ExchangeName = exchangeName,
                        RoutingKey = routingKey
                    });
                }

                _logger.LogDebug("[RabbitMQMessageQueueService] 绑定队列成功 - QueueName: {QueueName}, ExchangeName: {ExchangeName}, RoutingKey: {RoutingKey}",
                    queueName, exchangeName, routingKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitMQMessageQueueService] 绑定队列失败 - QueueName: {QueueName}, ExchangeName: {ExchangeName}, RoutingKey: {RoutingKey}",
                    queueName, exchangeName, routingKey);
                throw;
            }
        }

        #endregion

        #region Publish / 发布

        /// <summary>
        /// Publish message to exchange / 向交换机发布消息
        /// </summary>
        public async Task PublishAsync(string exchangeName, string routingKey, byte[] message, MessageProperties properties = null)
        {
            if (string.IsNullOrEmpty(exchangeName))
            {
                throw new ArgumentException("Exchange name cannot be null or empty", nameof(exchangeName));
            }
            if (string.IsNullOrEmpty(routingKey))
            {
                throw new ArgumentException("Routing key cannot be null or empty", nameof(routingKey));
            }
            if (message == null || message.Length == 0)
            {
                throw new ArgumentException("Message cannot be null or empty", nameof(message));
            }

            // 确保连接就绪，带重试
            int retryCount = 0;
            const int maxRetries = 3;
            while (retryCount <= maxRetries)
            {
                try
                {
                    await EnsureConnectedAsync().ConfigureAwait(false);

                    if (!IsConnected())
                    {
                        throw new InvalidOperationException("RabbitMQ channel is not open");
                    }

                    break;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    if (retryCount > maxRetries)
                    {
                        _logger.LogError(ex, "[RabbitMQMessageQueueService] 连接准备失败，已达到最大重试次数");
                        throw new InvalidOperationException("Failed to prepare RabbitMQ channel after retries", ex);
                    }
                    await Task.Delay(100 * retryCount).ConfigureAwait(false);
                }
            }

            // 构建消息属性
            var basicProperties = new BasicProperties();
            if (properties != null)
            {
                basicProperties.MessageId = properties.MessageId;
                basicProperties.CorrelationId = properties.CorrelationId;
                basicProperties.ReplyTo = properties.ReplyTo;

                if (properties.Timestamp.HasValue)
                {
                    basicProperties.Timestamp = new AmqpTimestamp(
                        (long)(properties.Timestamp.Value - new DateTime(1970, 1, 1)).TotalSeconds);
                }

                if (properties.Headers != null && properties.Headers.Count > 0)
                {
                    basicProperties.Headers = new Dictionary<string, object>();
                    foreach (var header in properties.Headers)
                    {
                        basicProperties.Headers[header.Key] = header.Value;
                    }
                }
            }

            try
            {
                // 再次检查连接状态
                if (!IsConnected())
                {
                    throw new InvalidOperationException("RabbitMQ channel closed before publishing");
                }

                await _channel.BasicPublishAsync(
                    exchange: exchangeName,
                    routingKey: routingKey,
                    mandatory: false,
                    basicProperties: basicProperties,
                    body: new ReadOnlyMemory<byte>(message)).ConfigureAwait(false);

                _logger.LogTrace("[RabbitMQMessageQueueService] 消息发布成功 - ExchangeName: {ExchangeName}, RoutingKey: {RoutingKey}, MessageSize: {MessageSize}",
                    exchangeName, routingKey, message.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitMQMessageQueueService] 消息发布失败 - ExchangeName: {ExchangeName}, RoutingKey: {RoutingKey}",
                    exchangeName, routingKey);
                throw;
            }
        }

        #endregion

        #region Consume / 消费

        /// <summary>
        /// Consume messages from queue / 从队列消费消息
        /// </summary>
        public async Task ConsumeAsync(string queueName, Func<byte[], MessageProperties, Task<bool>> handler, bool autoAck = false, string currentNodeId = null)
        {
            if (string.IsNullOrEmpty(queueName))
            {
                throw new ArgumentException("Queue name cannot be null or empty", nameof(queueName));
            }
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            await EnsureConnectedAsync().ConfigureAwait(false);
            await InternalConsumeAsync(queueName, handler, autoAck, currentNodeId, skipCheck: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Internal consume logic / 内部消费逻辑
        /// </summary>
        private async Task InternalConsumeAsync(string queueName, Func<byte[], MessageProperties, Task<bool>> handler,
            bool autoAck, string currentNodeId, bool skipCheck)
        {
            await _consumerLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // 检查是否已存在消费者
                if (!skipCheck && _consumers.ContainsKey(queueName) && _consumerTags.ContainsKey(queueName))
                {
                    try
                    {
                        var queueInfo = await _channel.QueueDeclarePassiveAsync(queueName).ConfigureAwait(false);
                        if (queueInfo.ConsumerCount > 0)
                        {
                            _logger.LogDebug("[RabbitMQMessageQueueService] 消费者已存在，跳过创建 - QueueName: {QueueName}", queueName);
                            return;
                        }
                    }
                    catch
                    {
                        // 验证失败，继续创建
                    }
                }

                // 取消旧消费者（如果存在）
                if (_consumerTags.TryGetValue(queueName, out var oldTag) && !string.IsNullOrEmpty(oldTag))
                {
                    try
                    {
                        await _channel.BasicCancelAsync(oldTag).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 忽略取消失败
                    }
                }

                // 保存消费者信息
                _consumerInfos[queueName] = new ConsumerInfo
                {
                    Handler = handler,
                    AutoAck = autoAck,
                    CurrentNodeId = currentNodeId
                };

                // 创建消费者
                var consumer = new AsyncEventingBasicConsumer(_channel);
                _consumers[queueName] = consumer;

                // 设置消息处理事件
                consumer.ReceivedAsync += async (model, ea) =>
                {
                    try
                    {
                        var properties = new MessageProperties
                        {
                            MessageId = ea.BasicProperties.MessageId,
                            CorrelationId = ea.BasicProperties.CorrelationId,
                            ReplyTo = ea.BasicProperties.ReplyTo,
                            DeliveryTag = ea.DeliveryTag,
                            RoutingKey = ea.RoutingKey,
                            Headers = new Dictionary<string, object>()
                        };

                        if (ea.BasicProperties.Timestamp.UnixTime > 0)
                        {
                            properties.Timestamp = new DateTime(1970, 1, 1).AddSeconds(ea.BasicProperties.Timestamp.UnixTime);
                        }

                        if (ea.BasicProperties.Headers != null)
                        {
                            foreach (var header in ea.BasicProperties.Headers)
                            {
                                properties.Headers[header.Key.ToString()] = header.Value;
                            }
                        }

                        // 早期过滤：检查是否来自自己
                        if (!string.IsNullOrEmpty(currentNodeId) &&
                            properties.Headers.TryGetValue("FromNodeId", out var fromNodeIdObj) &&
                            fromNodeIdObj?.ToString() == currentNodeId)
                        {
                            if (!autoAck)
                            {
                                await _channel.BasicAckAsync(ea.DeliveryTag, false).ConfigureAwait(false);
                            }
                            return;
                        }

                        // 处理消息
                        var success = await handler(ea.Body.ToArray(), properties).ConfigureAwait(false);

                        // 确认或拒绝消息
                        if (!autoAck)
                        {
                            if (success)
                            {
                                await _channel.BasicAckAsync(ea.DeliveryTag, false).ConfigureAwait(false);
                            }
                            else
                            {
                                await _channel.BasicNackAsync(ea.DeliveryTag, false, true).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RabbitMQMessageQueueService] 处理消息时发生异常 - QueueName: {QueueName}, DeliveryTag: {DeliveryTag}",
                            queueName, ea.DeliveryTag);
                        if (!autoAck)
                        {
                            try
                            {
                                await _channel.BasicNackAsync(ea.DeliveryTag, false, true).ConfigureAwait(false);
                            }
                            catch
                            {
                                // 忽略确认失败
                            }
                        }
                    }
                };

                // 启动消费者
                var consumerTag = await _channel.BasicConsumeAsync(queueName, autoAck, consumer).ConfigureAwait(false);

                if (string.IsNullOrEmpty(consumerTag))
                {
                    _consumers.Remove(queueName);
                    _consumerInfos.Remove(queueName);
                    throw new InvalidOperationException($"Consumer registration failed: returned empty consumer tag for queue {queueName}");
                }

                _consumerTags[queueName] = consumerTag;

                _logger.LogInformation("[RabbitMQMessageQueueService] 消费者创建成功 - QueueName: {QueueName}, ConsumerTag: {ConsumerTag}, AutoAck: {AutoAck}",
                    queueName, consumerTag, autoAck);
            }
            finally
            {
                _consumerLock.Release();
            }
        }

        /// <summary>
        /// Get queue consumer count / 获取队列消费者数量
        /// </summary>
        public async Task<uint> GetQueueConsumerCountAsync(string queueName)
        {
            if (string.IsNullOrEmpty(queueName))
            {
                throw new ArgumentException("Queue name cannot be null or empty", nameof(queueName));
            }

            await EnsureConnectedAsync().ConfigureAwait(false);

            try
            {
                var queueInfo = await _channel.QueueDeclarePassiveAsync(queueName).ConfigureAwait(false);
                return queueInfo.ConsumerCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitMQMessageQueueService] 获取队列消费者数量失败 - QueueName: {QueueName}", queueName);
                throw;
            }
        }

        /// <summary>
        /// Acknowledge message / 确认消息
        /// </summary>
        public async Task AckAsync(ulong deliveryTag)
        {
            await EnsureConnectedAsync().ConfigureAwait(false);
            await _channel.BasicAckAsync(deliveryTag, false).ConfigureAwait(false);
        }

        /// <summary>
        /// Reject message / 拒绝消息
        /// </summary>
        public async Task RejectAsync(ulong deliveryTag, bool requeue = false)
        {
            await EnsureConnectedAsync().ConfigureAwait(false);
            await _channel.BasicNackAsync(deliveryTag, false, requeue).ConfigureAwait(false);
        }

        #endregion

        #region Dispose / 释放资源

        /// <summary>
        /// Dispose / 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisconnectAsync().GetAwaiter().GetResult();

            _connectionLock?.Dispose();
            _consumerLock?.Dispose();
        }

        #endregion
    }
}
