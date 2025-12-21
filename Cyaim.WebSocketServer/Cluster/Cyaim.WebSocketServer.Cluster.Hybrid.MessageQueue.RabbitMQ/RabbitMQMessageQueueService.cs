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
        private IConnection _connection;
        // RabbitMQ.Client 7.0+ uses IChannel instead of IModel
        // RabbitMQ.Client 7.0+ 使用 IChannel 替代 IModel
        private IChannel _channel;
        // RabbitMQ.Client 7.0+ uses AsyncEventingBasicConsumer
        // RabbitMQ.Client 7.0+ 使用 AsyncEventingBasicConsumer
        private readonly Dictionary<string, AsyncEventingBasicConsumer> _consumers;
        // Store consumer handlers for reconnection / 存储消费者处理器以便重新连接
        private readonly Dictionary<string, (Func<byte[], MessageProperties, Task<bool>> Handler, bool AutoAck)> _consumerHandlers;
        // Store current node IDs for filtering self-messages / 存储当前节点 ID 以便过滤自己的消息
        private readonly Dictionary<string, string> _currentNodeIds;
        // Store declared exchanges for reconnection / 存储已声明的交换机以便重新连接
        private readonly Dictionary<string, (string ExchangeType, bool Durable)> _declaredExchanges;
        // Store declared queues for reconnection / 存储已声明的队列以便重新连接
        private readonly Dictionary<string, (bool Durable, bool Exclusive, bool AutoDelete)> _declaredQueues;
        // Store queue bindings for reconnection / 存储队列绑定以便重新连接
        private readonly Dictionary<string, List<(string ExchangeName, string RoutingKey)>> _queueBindings;
        private readonly object _reconnectLock = new object();
        private bool _disposed = false;
        private readonly SemaphoreSlim _channelLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="connectionString">RabbitMQ connection string / RabbitMQ 连接字符串</param>
        public RabbitMQMessageQueueService(ILogger<RabbitMQMessageQueueService> logger, string connectionString)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _consumers = new Dictionary<string, AsyncEventingBasicConsumer>();
            _consumerHandlers = new Dictionary<string, (Func<byte[], MessageProperties, Task<bool>>, bool)>();
            _currentNodeIds = new Dictionary<string, string>();
            _declaredExchanges = new Dictionary<string, (string, bool)>();
            _declaredQueues = new Dictionary<string, (bool, bool, bool)>();
            _queueBindings = new Dictionary<string, List<(string, string)>>();
        }

        /// <summary>
        /// Connect to message queue / 连接到消息队列
        /// </summary>
        public async Task ConnectAsync()
        {
            await _channelLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_connection != null && _connection.IsOpen && _channel != null && _channel.IsOpen)
                {
                    _logger.LogDebug($"[RabbitMQMessageQueueService] RabbitMQ 已连接，跳过重连 - ConnectionIsOpen: {_connection.IsOpen}, ChannelIsOpen: {_channel.IsOpen}");
                    return;
                }

                // 清理旧连接 / Cleanup old connection
                try
                {
                    if (_channel != null)
                    {
                        await _channel.CloseAsync().ConfigureAwait(false);
                        _channel.Dispose();
                    }
                }
                catch { }

                try
                {
                    if (_connection != null)
                    {
                        await _connection.CloseAsync().ConfigureAwait(false);
                        _connection.Dispose();
                    }
                }
                catch { }

                _connection = null;
                _channel = null;

                try
                {
                    _logger.LogWarning($"[RabbitMQMessageQueueService] 开始连接 RabbitMQ - ConnectionString: {_connectionString}");
                    var factory = new ConnectionFactory { Uri = new Uri(_connectionString) };
                    _connection = await factory.CreateConnectionAsync().ConfigureAwait(false);
                    _channel = await _connection.CreateChannelAsync().ConfigureAwait(false);
                    _logger.LogWarning($"[RabbitMQMessageQueueService] RabbitMQ 连接成功 - ConnectionIsOpen: {_connection.IsOpen}, ChannelIsOpen: {_channel.IsOpen}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[RabbitMQMessageQueueService] RabbitMQ 连接失败 - ConnectionString: {_connectionString}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                    throw;
                }
            }
            finally
            {
                _channelLock.Release();
            }
        }

        /// <summary>
        /// Re-declare exchanges and queues after reconnection / 重新连接后重新声明交换机和队列
        /// </summary>
        private async Task RedeclareExchangesAndQueuesAsync()
        {
            if (_channel == null || !_channel.IsOpen)
            {
                return;
            }

            try
            {
                // Re-declare exchanges / 重新声明交换机
                foreach (var kvp in _declaredExchanges)
                {
                    try
                    {
                        var exchangeName = kvp.Key;
                        var (exchangeType, durable) = kvp.Value;
                        _logger.LogWarning($"[RabbitMQMessageQueueService] 重新声明交换机 - ExchangeName: {exchangeName}, ExchangeType: {exchangeType}, Durable: {durable}");
                        await _channel.ExchangeDeclareAsync(exchangeName, exchangeType, durable: durable, autoDelete: false);
                        _logger.LogWarning($"[RabbitMQMessageQueueService] 交换机重新声明成功 - ExchangeName: {exchangeName}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"[RabbitMQMessageQueueService] 重新声明交换机失败 - ExchangeName: {kvp.Key}, Error: {ex.Message}");
                    }
                }

                // Re-declare queues / 重新声明队列
                foreach (var kvp in _declaredQueues)
                {
                    try
                    {
                        var queueName = kvp.Key;
                        var (durable, exclusive, autoDelete) = kvp.Value;
                        _logger.LogWarning($"[RabbitMQMessageQueueService] 重新声明队列 - QueueName: {queueName}, Durable: {durable}, Exclusive: {exclusive}, AutoDelete: {autoDelete}");
                        await _channel.QueueDeclareAsync(
                            queue: queueName,
                            durable: durable,
                            exclusive: exclusive,
                            autoDelete: autoDelete,
                            arguments: null);
                        _logger.LogWarning($"[RabbitMQMessageQueueService] 队列重新声明成功 - QueueName: {queueName}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"[RabbitMQMessageQueueService] 重新声明队列失败 - QueueName: {kvp.Key}, Error: {ex.Message}");
                    }
                }

                // Re-bind queues / 重新绑定队列
                foreach (var kvp in _queueBindings)
                {
                    var queueName = kvp.Key;
                    foreach (var (exchangeName, routingKey) in kvp.Value)
                    {
                        try
                        {
                            _logger.LogWarning($"[RabbitMQMessageQueueService] 重新绑定队列到交换机 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}");
                            await _channel.QueueBindAsync(queueName, exchangeName, routingKey);
                            _logger.LogWarning($"[RabbitMQMessageQueueService] 队列重新绑定成功 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"[RabbitMQMessageQueueService] 重新绑定队列失败 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, Error: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RabbitMQMessageQueueService] 重新声明交换机和队列时发生异常 - Error: {ex.Message}, StackTrace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Recreate consumers after reconnection / 重新连接后重新创建消费者
        /// </summary>
        private async Task RecreateConsumersAsync()
        {
            if (_channel == null || !_channel.IsOpen)
            {
                return;
            }

            var queueNames = new List<string>(_consumerHandlers.Keys);
            foreach (var queueName in queueNames)
            {
                try
                {
                    var (handler, autoAck) = _consumerHandlers[queueName];
                    _currentNodeIds.TryGetValue(queueName, out var currentNodeId);
                    _logger.LogWarning($"[RabbitMQMessageQueueService] 重新创建消费者 - QueueName: {queueName}, CurrentNodeId: {currentNodeId}");

                    // Remove old consumer from dictionary / 从字典中移除旧消费者
                    _consumers.Remove(queueName);

                    // Recreate consumer / 重新创建消费者
                    var consumer = new AsyncEventingBasicConsumer(_channel);
                    _consumers[queueName] = consumer;

                    consumer.ReceivedAsync += async (model, ea) =>
                    {
                        try
                        {
                            _logger.LogTrace($"[RabbitMQMessageQueueService] 收到消息 - QueueName: {queueName}, RoutingKey: {ea.RoutingKey}, Exchange: {ea.Exchange}, DeliveryTag: {ea.DeliveryTag}, MessageSize: {ea.Body.Length} bytes");

                            var properties = new MessageProperties
                            {
                                MessageId = ea.BasicProperties.MessageId,
                                CorrelationId = ea.BasicProperties.CorrelationId,
                                ReplyTo = ea.BasicProperties.ReplyTo,
                                DeliveryTag = ea.DeliveryTag,
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

                            // Early filter: Check if message is from self (for broadcast messages) / 早期过滤：检查消息是否来自自己（用于广播消息）
                            if (!string.IsNullOrEmpty(currentNodeId) &&
                                properties.Headers.TryGetValue("FromNodeId", out var fromNodeIdObj) &&
                                fromNodeIdObj?.ToString() == currentNodeId)
                            {
                                // Message is from self, ACK and skip processing / 消息来自自己，确认并跳过处理
                                if (!autoAck)
                                {
                                    await _channel.BasicAckAsync(ea.DeliveryTag, false);
                                }
                                _logger.LogTrace($"[RabbitMQMessageQueueService] 跳过来自自己的消息（早期过滤）- QueueName: {queueName}, MessageId: {properties.MessageId}, FromNodeId: {fromNodeIdObj}, CurrentNodeId: {currentNodeId}, DeliveryTag: {ea.DeliveryTag}");
                                return;
                            }

                            _logger.LogTrace($"[RabbitMQMessageQueueService] 开始处理消息 - QueueName: {queueName}, MessageId: {properties.MessageId}, DeliveryTag: {ea.DeliveryTag}");
                            var success = await handler(ea.Body.ToArray(), properties);
                            _logger.LogTrace($"[RabbitMQMessageQueueService] 消息处理完成 - QueueName: {queueName}, MessageId: {properties.MessageId}, Success: {success}, DeliveryTag: {ea.DeliveryTag}");

                            if (!autoAck)
                            {
                                if (success)
                                {
                                    await _channel.BasicAckAsync(ea.DeliveryTag, false);
                                    _logger.LogTrace($"[RabbitMQMessageQueueService] 消息已确认 - QueueName: {queueName}, DeliveryTag: {ea.DeliveryTag}");
                                }
                                else
                                {
                                    await _channel.BasicNackAsync(ea.DeliveryTag, false, true); // Requeue / 重新入队
                                    _logger.LogWarning($"[RabbitMQMessageQueueService] 消息已拒绝并重新入队 - QueueName: {queueName}, DeliveryTag: {ea.DeliveryTag}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"[RabbitMQMessageQueueService] 处理消息时发生异常 - QueueName: {queueName}, DeliveryTag: {ea.DeliveryTag}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                            if (!autoAck)
                            {
                                await _channel.BasicNackAsync(ea.DeliveryTag, false, true);
                            }
                        }
                    };

                    var consumerTag = await _channel.BasicConsumeAsync(queueName, autoAck, consumer);
                    _logger.LogWarning($"[RabbitMQMessageQueueService] 消费者重新创建成功 - QueueName: {queueName}, ConsumerTag: {consumerTag}, AutoAck: {autoAck}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[RabbitMQMessageQueueService] 重新创建消费者失败 - QueueName: {queueName}, Error: {ex.Message}");
                }
            }
        }

        private volatile bool _isReconnecting = false;

        /// <summary>
        /// Verify connection is ready / 验证连接已就绪
        /// </summary>
        public async Task VerifyConnectionAsync()
        {
            await EnsureConnectedAsync();

            // Additional verification: ensure channel is truly ready / 额外验证：确保 channel 真正就绪
            if (_connection == null || !_connection.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ connection is not ready");
            }
            if (_channel == null || !_channel.IsOpen)
            {
                throw new InvalidOperationException("RabbitMQ channel is not ready");
            }

            // Test channel with a simple operation / 使用简单操作测试 channel
            try
            {
                var channelNumber = _channel.ChannelNumber;
                _logger.LogDebug($"[RabbitMQMessageQueueService] 连接验证成功 - ConnectionIsOpen: {_connection.IsOpen}, ChannelIsOpen: {_channel.IsOpen}, ChannelNumber: {channelNumber}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitMQMessageQueueService] Channel 验证失败");
                throw new InvalidOperationException("RabbitMQ channel verification failed", ex);
            }
        }

        /// <summary>
        /// Ensure connection and channel are open, reconnect if necessary / 确保连接和 channel 已打开，必要时重新连接
        /// </summary>
        private async Task EnsureConnectedAsync()
        {
            // Quick check first / 先快速检查
            if (_connection != null && _connection.IsOpen && _channel != null && _channel.IsOpen)
            {
                return;
            }

            // Use lock to prevent concurrent reconnection attempts / 使用锁防止并发重连尝试
            if (!_isReconnecting)
            {
                lock (_reconnectLock)
                {
                    // Double-check after acquiring lock / 获取锁后再次检查
                    if (!_isReconnecting && (_connection == null || !_connection.IsOpen || _channel == null || !_channel.IsOpen))
                    {
                        _isReconnecting = true;
                        _logger.LogWarning($"[RabbitMQMessageQueueService] 检测到连接或 channel 已关闭，尝试重新连接 - ConnectionIsNull: {_connection == null}, ConnectionIsOpen: {_connection?.IsOpen ?? false}, ChannelIsNull: {_channel == null}, ChannelIsOpen: {_channel?.IsOpen ?? false}");
                    }
                    else
                    {
                        return; // Another thread is already reconnecting or connection is now open / 另一个线程正在重连或连接已打开
                    }
                }
            }
            else
            {
                // Wait for reconnection to complete / 等待重连完成
                int waitCount = 0;
                while (_isReconnecting && waitCount < 50) // Wait up to 5 seconds / 最多等待 5 秒
                {
                    await Task.Delay(100);
                    waitCount++;
                    // Check if connection is now open / 检查连接是否已打开
                    if (_connection != null && _connection.IsOpen && _channel != null && _channel.IsOpen)
                    {
                        return;
                    }
                }
                if (_isReconnecting)
                {
                    _logger.LogWarning($"[RabbitMQMessageQueueService] 等待重连超时，继续尝试重连");
                }
            }

            try
            {
                await ConnectAsync();
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        /// <summary>
        /// Disconnect from message queue / 断开消息队列连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            // Clear consumers but keep handlers for reconnection / 清除消费者但保留处理器以便重新连接
            _consumers.Clear();
            // Note: We keep _consumerHandlers so we can recreate consumers after reconnection
            // 注意：我们保留 _consumerHandlers 以便在重新连接后重新创建消费者

            if (_channel != null)
            {
                try
                {
                    if (_channel.IsOpen)
                    {
                        await _channel.CloseAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[RabbitMQMessageQueueService] 关闭 channel 时发生错误");
                }
                _channel.Dispose();
                _channel = null;
            }

            if (_connection != null)
            {
                try
                {
                    if (_connection.IsOpen)
                    {
                        await _connection.CloseAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[RabbitMQMessageQueueService] 关闭连接时发生错误");
                }
                _connection.Dispose();
                _connection = null;
                _logger.LogInformation("Disconnected from RabbitMQ");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Declare an exchange / 声明交换机
        /// </summary>
        public async Task DeclareExchangeAsync(string exchangeName, string exchangeType, bool durable = true)
        {
            await EnsureChannelAsync().ConfigureAwait(false);

            _logger.LogWarning($"[RabbitMQMessageQueueService] 声明交换机 - ExchangeName: {exchangeName}, ExchangeType: {exchangeType}, Durable: {durable}");

            await _channel.ExchangeDeclareAsync(exchangeName, exchangeType, durable: durable, autoDelete: false);

            _logger.LogWarning($"[RabbitMQMessageQueueService] 交换机声明成功 - ExchangeName: {exchangeName}, ExchangeType: {exchangeType}");
        }

        /// <summary>
        /// Declare a queue / 声明队列
        /// </summary>
        public async Task<string> DeclareQueueAsync(string queueName, bool durable = false, bool exclusive = false, bool autoDelete = true)
        {
            if (string.IsNullOrEmpty(queueName))
            {
                throw new ArgumentException("Queue name cannot be null or empty", nameof(queueName));
            }

            await EnsureChannelAsync().ConfigureAwait(false);

            if (_channel == null || !_channel.IsOpen)
            {
                _logger.LogError($"[RabbitMQMessageQueueService] Channel 未打开，无法声明队列 - QueueName: {queueName}");
                throw new InvalidOperationException("RabbitMQ channel is not open");
            }

            _logger.LogWarning($"[RabbitMQMessageQueueService] 开始声明队列 - QueueName: {queueName}, Durable: {durable}, Exclusive: {exclusive}, AutoDelete: {autoDelete}, ChannelIsOpen: {_channel.IsOpen}");

            try
            {
                var result = await _channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: durable,
                    exclusive: exclusive,
                    autoDelete: autoDelete,
                    arguments: null);

                // 保存队列信息以便重新连接时恢复
                // Save queue info for reconnection recovery
                _declaredQueues[queueName] = (durable, exclusive, autoDelete);
                _logger.LogWarning($"[RabbitMQMessageQueueService] 队列信息已保存 - QueueName: {queueName}, TotalQueues: {_declaredQueues.Count}");

                _logger.LogWarning($"[RabbitMQMessageQueueService] 队列声明成功 - QueueName: {result.QueueName}, ConsumerCount: {result.ConsumerCount}, MessageCount: {result.MessageCount}, Durable: {durable}, Exclusive: {exclusive}, AutoDelete: {autoDelete}");

                // Verify queue exists by checking result / 通过检查结果验证队列是否存在
                if (string.IsNullOrEmpty(result.QueueName))
                {
                    _logger.LogError($"[RabbitMQMessageQueueService] 队列声明返回空名称 - RequestedQueueName: {queueName}");
                    throw new InvalidOperationException($"Queue declaration returned empty queue name for requested name: {queueName}");
                }

                if (result.QueueName != queueName)
                {
                    _logger.LogWarning($"[RabbitMQMessageQueueService] 队列名称不匹配 - RequestedQueueName: {queueName}, ActualQueueName: {result.QueueName}");
                    // Update saved queue info with actual queue name / 使用实际队列名称更新保存的队列信息
                    _declaredQueues.Remove(queueName);
                    _declaredQueues[result.QueueName] = (durable, exclusive, autoDelete);
                }

                return result.QueueName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RabbitMQMessageQueueService] 队列声明失败 - QueueName: {queueName}, Durable: {durable}, Exclusive: {exclusive}, AutoDelete: {autoDelete}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
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

            await EnsureChannelAsync().ConfigureAwait(false);

            if (_channel == null || !_channel.IsOpen)
            {
                _logger.LogError($"[RabbitMQMessageQueueService] Channel 未打开，无法绑定队列 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}");
                throw new InvalidOperationException("RabbitMQ channel is not open");
            }

            _logger.LogWarning($"[RabbitMQMessageQueueService] 开始绑定队列到交换机 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, ChannelIsOpen: {_channel.IsOpen}");

            try
            {
                await _channel.QueueBindAsync(queueName, exchangeName, routingKey);
                _logger.LogWarning($"[RabbitMQMessageQueueService] 队列绑定操作完成 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}");

                // 保存绑定信息以便重新连接时恢复
                // Save binding info for reconnection recovery
                if (!_queueBindings.ContainsKey(queueName))
                {
                    _queueBindings[queueName] = new List<(string ExchangeName, string RoutingKey)>();
                }

                // 检查是否已存在相同的绑定，避免重复添加
                // Check if the same binding already exists to avoid duplicates
                var existingBinding = _queueBindings[queueName].FirstOrDefault(b => b.ExchangeName == exchangeName && b.RoutingKey == routingKey);
                if (existingBinding == default)
                {
                    _queueBindings[queueName].Add((exchangeName, routingKey));
                    _logger.LogWarning($"[RabbitMQMessageQueueService] 绑定信息已保存 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, TotalBindings: {_queueBindings[queueName].Count}");
                }
                else
                {
                    _logger.LogTrace($"[RabbitMQMessageQueueService] 绑定信息已存在，跳过保存 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}");
                }

                _logger.LogWarning($"[RabbitMQMessageQueueService] 队列绑定成功 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, SavedBindings: {_queueBindings[queueName].Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RabbitMQMessageQueueService] 队列绑定失败 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                throw;
            }
        }

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

            // Ensure channel is ready with retry / 确保 channel 就绪，带重试
            int retryCount = 0;
            const int maxRetries = 2;
            while (retryCount <= maxRetries)
            {
                try
                {
                    await EnsureChannelAsync().ConfigureAwait(false);

                    if (_channel == null || !_channel.IsOpen)
                    {
                        throw new InvalidOperationException("RabbitMQ channel is not open");
                    }
                    break; // Success / 成功
                }
                catch (Exception ex)
                {
                    retryCount++;
                    if (retryCount > maxRetries)
                    {
                        _logger.LogError(ex, $"[RabbitMQMessageQueueService] Channel 准备失败，已达到最大重试次数 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, MaxRetries: {maxRetries}");
                        throw new InvalidOperationException("Failed to prepare RabbitMQ channel after retries", ex);
                    }
                    _logger.LogWarning(ex, $"[RabbitMQMessageQueueService] Channel 准备失败，重试中 ({retryCount}/{maxRetries}) - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, Error: {ex.Message}");
                    await Task.Delay(100 * retryCount); // Exponential backoff / 指数退避
                }
            }

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

            _logger.LogWarning($"[RabbitMQMessageQueueService] 发布消息 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, MessageId: {properties?.MessageId}, MessageSize: {message.Length} bytes, ChannelIsOpen: {_channel.IsOpen}");

            try
            {
                // Double-check channel is still open before publishing / 发布前再次检查 channel 是否仍然打开
                if (_channel == null || !_channel.IsOpen)
                {
                    _logger.LogError($"[RabbitMQMessageQueueService] Channel 在发布前关闭 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, MessageId: {properties?.MessageId}");
                    throw new InvalidOperationException("RabbitMQ channel closed before publishing");
                }

                await _channel.BasicPublishAsync(
                    exchange: exchangeName,
                    routingKey: routingKey,
                    mandatory: false,
                    basicProperties: basicProperties,
                    body: new ReadOnlyMemory<byte>(message));

                _logger.LogTrace($"[RabbitMQMessageQueueService] 消息发布成功 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, MessageId: {properties?.MessageId}, MessageSize: {message.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RabbitMQMessageQueueService] 消息发布失败 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, MessageId: {properties?.MessageId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");

                // If channel is closed, mark for reconnection / 如果 channel 关闭，标记需要重新连接
                if (_channel == null || !_channel.IsOpen)
                {
                    _logger.LogWarning($"[RabbitMQMessageQueueService] Channel 已关闭，标记需要重新连接 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}");
                    // Channel will be recreated on next EnsureChannelAsync call / Channel 将在下次 EnsureChannelAsync 调用时重新创建
                }

                throw;
            }
        }

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

            await EnsureChannelAsync().ConfigureAwait(false);

            if (_channel == null || !_channel.IsOpen)
            {
                _logger.LogError($"[RabbitMQMessageQueueService] Channel 未打开，无法启动消费者 - QueueName: {queueName}");
                throw new InvalidOperationException("RabbitMQ channel is not open");
            }

            // Verify queue exists before consuming / 消费前验证队列是否存在
            try
            {
                _logger.LogWarning($"[RabbitMQMessageQueueService] 验证队列是否存在 - QueueName: {queueName}");
                var queueDeclareResult = await _channel.QueueDeclarePassiveAsync(queueName);
                _logger.LogWarning($"[RabbitMQMessageQueueService] 队列验证成功 - QueueName: {queueName}, ConsumerCount: {queueDeclareResult.ConsumerCount}, MessageCount: {queueDeclareResult.MessageCount}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RabbitMQMessageQueueService] 队列验证失败，尝试重新声明 - QueueName: {queueName}, Error: {ex.Message}");
                // Queue doesn't exist, try to redeclare it / 队列不存在，尝试重新声明
                if (_declaredQueues.TryGetValue(queueName, out var queueInfo))
                {
                    var (durable, exclusive, autoDelete) = queueInfo;
                    await DeclareQueueAsync(queueName, durable, exclusive, autoDelete);
                }
                else
                {
                    _logger.LogError($"[RabbitMQMessageQueueService] 队列信息未找到，无法重新声明 - QueueName: {queueName}");
                    throw new InvalidOperationException($"Queue {queueName} does not exist and cannot be redeclared", ex);
                }
            }

            // Store handler for reconnection / 存储处理器以便重新连接
            _consumerHandlers[queueName] = (handler, autoAck);

            // Store currentNodeId for filtering self-messages / 存储 currentNodeId 以便过滤自己的消息
            if (!string.IsNullOrEmpty(currentNodeId))
            {
                _currentNodeIds[queueName] = currentNodeId;
            }

            if (_consumers.ContainsKey(queueName))
            {
                _logger.LogWarning($"[RabbitMQMessageQueueService] 消费者已存在，跳过重新创建 - QueueName: {queueName}");
                return;
            }

            // Use AsyncEventingBasicConsumer for RabbitMQ.Client 7.0+
            // 使用 AsyncEventingBasicConsumer 支持 RabbitMQ.Client 7.0+
            var consumer = new AsyncEventingBasicConsumer(_channel);
            _consumers[queueName] = consumer;

            _logger.LogWarning($"[RabbitMQMessageQueueService] 准备启动消费者 - QueueName: {queueName}, ChannelIsOpen: {_channel.IsOpen}, CurrentConsumers: {_consumers.Count}");

            consumer.ReceivedAsync += async (model, ea) =>
            {
                try
                {
                    _logger.LogWarning($"[RabbitMQMessageQueueService] 收到消息 - QueueName: {queueName}, RoutingKey: {ea.RoutingKey}, Exchange: {ea.Exchange}, DeliveryTag: {ea.DeliveryTag}, MessageSize: {ea.Body.Length} bytes");

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

                    // Early filter: Check if message is from self (for broadcast messages) / 早期过滤：检查消息是否来自自己（用于广播消息）
                    if (!string.IsNullOrEmpty(currentNodeId) &&
                        properties.Headers.TryGetValue("FromNodeId", out var fromNodeIdObj) &&
                        fromNodeIdObj?.ToString() == currentNodeId)
                    {
                        // Message is from self, ACK and skip processing / 消息来自自己，确认并跳过处理
                        if (!autoAck)
                        {
                            await _channel.BasicAckAsync(ea.DeliveryTag, false);
                        }
                        _logger.LogTrace($"[RabbitMQMessageQueueService] 跳过来自自己的消息（早期过滤）- QueueName: {queueName}, MessageId: {properties.MessageId}, FromNodeId: {fromNodeIdObj}, CurrentNodeId: {currentNodeId}, DeliveryTag: {ea.DeliveryTag}");
                        return;
                    }

                    _logger.LogWarning($"[RabbitMQMessageQueueService] 开始处理消息 - QueueName: {queueName}, MessageId: {properties.MessageId}, DeliveryTag: {ea.DeliveryTag}");
                    var success = await handler(ea.Body.ToArray(), properties);
                    _logger.LogWarning($"[RabbitMQMessageQueueService] 消息处理完成 - QueueName: {queueName}, MessageId: {properties.MessageId}, Success: {success}, DeliveryTag: {ea.DeliveryTag}");

                    if (!autoAck)
                    {
                        if (success)
                        {
                            await _channel.BasicAckAsync(ea.DeliveryTag, false);
                            _logger.LogDebug($"[RabbitMQMessageQueueService] 消息已确认 - QueueName: {queueName}, DeliveryTag: {ea.DeliveryTag}");
                        }
                        else
                        {
                            await _channel.BasicNackAsync(ea.DeliveryTag, false, true); // Requeue / 重新入队
                            _logger.LogWarning($"[RabbitMQMessageQueueService] 消息已拒绝并重新入队 - QueueName: {queueName}, DeliveryTag: {ea.DeliveryTag}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[RabbitMQMessageQueueService] 处理消息时发生异常 - QueueName: {queueName}, DeliveryTag: {ea.DeliveryTag}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                    if (!autoAck)
                    {
                        await _channel.BasicNackAsync(ea.DeliveryTag, false, true);
                    }
                }
            };

            _logger.LogWarning($"[RabbitMQMessageQueueService] 准备启动消费者 - QueueName: {queueName}, AutoAck: {autoAck}, ChannelIsOpen: {_channel.IsOpen}, CurrentConsumers: {_consumers.Count}");

            var consumerTag = await _channel.BasicConsumeAsync(queueName, autoAck, consumer);

            _logger.LogWarning($"[RabbitMQMessageQueueService] 消费者启动成功 - QueueName: {queueName}, ConsumerTag: {consumerTag}, AutoAck: {autoAck}, TotalConsumers: {_consumers.Count}");

            // Verify consumer is actually consuming by checking queue status / 通过检查队列状态验证消费者确实在消费
            // Note: QueueDeclarePassiveAsync will throw if queue doesn't exist
            // 注意：如果队列不存在，QueueDeclarePassiveAsync 会抛出异常
            try
            {
                var queueInfo = await _channel.QueueDeclarePassiveAsync(queueName);
                _logger.LogWarning($"[RabbitMQMessageQueueService] 队列状态确认 - QueueName: {queueName}, ConsumerCount: {queueInfo.ConsumerCount}, MessageCount: {queueInfo.MessageCount}, QueueExists: true");

                if (queueInfo.ConsumerCount == 0)
                {
                    _logger.LogWarning($"[RabbitMQMessageQueueService] 警告：队列消费者数量为 0，但消费者已启动 - QueueName: {queueName}, ConsumerTag: {consumerTag}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RabbitMQMessageQueueService] 队列状态确认失败 - QueueName: {queueName}, Error: {ex.Message}");
                // Don't throw - consumer might still work / 不抛出异常 - 消费者可能仍然有效
            }
        }

        /// <summary>
        /// Acknowledge message / 确认消息
        /// </summary>
        public async Task AckAsync(ulong deliveryTag)
        {
            await EnsureChannelAsync().ConfigureAwait(false);

            await _channel.BasicAckAsync(deliveryTag, false);
        }

        /// <summary>
        /// Reject message / 拒绝消息
        /// </summary>
        public async Task RejectAsync(ulong deliveryTag, bool requeue = false)
        {
            await EnsureChannelAsync().ConfigureAwait(false);

            await _channel.BasicNackAsync(deliveryTag, false, requeue);
        }

        /// <summary>
        /// Ensure connection and channel are open / 确保连接和通道已打开
        /// </summary>
        private async Task EnsureChannelAsync()
        {
            if (_channel != null && _channel.IsOpen && _connection != null && _connection.IsOpen)
            {
                return;
            }

            await ConnectAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Dispose / 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DisconnectAsync().GetAwaiter().GetResult();
        }
    }
}

