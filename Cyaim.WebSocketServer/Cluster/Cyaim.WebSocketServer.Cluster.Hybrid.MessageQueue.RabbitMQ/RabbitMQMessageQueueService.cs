using System;
using System.Collections.Generic;
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
        /// Disconnect from message queue / 断开消息队列连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            // Clear consumers / 清除消费者
            _consumers.Clear();

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
            await EnsureChannelAsync().ConfigureAwait(false);

            _logger.LogWarning($"[RabbitMQMessageQueueService] 声明队列 - QueueName: {queueName}, Durable: {durable}, Exclusive: {exclusive}, AutoDelete: {autoDelete}");
            
            var result = await _channel.QueueDeclareAsync(
                queue: queueName,
                durable: durable,
                exclusive: exclusive,
                autoDelete: autoDelete,
                arguments: null);

            _logger.LogWarning($"[RabbitMQMessageQueueService] 队列声明成功 - QueueName: {result.QueueName}, ConsumerCount: {result.ConsumerCount}, MessageCount: {result.MessageCount}");
            
            return result.QueueName;
        }

        /// <summary>
        /// Bind queue to exchange / 将队列绑定到交换机
        /// </summary>
        public async Task BindQueueAsync(string queueName, string exchangeName, string routingKey)
        {
            await EnsureChannelAsync().ConfigureAwait(false);

            _logger.LogWarning($"[RabbitMQMessageQueueService] 绑定队列到交换机 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}");
            
            await _channel.QueueBindAsync(queueName, exchangeName, routingKey);
            
            _logger.LogWarning($"[RabbitMQMessageQueueService] 队列绑定成功 - QueueName: {queueName}, ExchangeName: {exchangeName}, RoutingKey: {routingKey}");
        }

        /// <summary>
        /// Publish message to exchange / 向交换机发布消息
        /// </summary>
        public async Task PublishAsync(string exchangeName, string routingKey, byte[] message, MessageProperties properties = null)
        {
            await EnsureChannelAsync().ConfigureAwait(false);

            if (_channel == null || !_channel.IsOpen)
            {
                _logger.LogError($"[RabbitMQMessageQueueService] Channel 未打开，无法发布消息 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}");
                throw new InvalidOperationException("RabbitMQ channel is not open");
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
                await _channel.BasicPublishAsync(
                    exchange: exchangeName,
                    routingKey: routingKey,
                    mandatory: false,
                    basicProperties: basicProperties,
                    body: new ReadOnlyMemory<byte>(message));
                
                _logger.LogWarning($"[RabbitMQMessageQueueService] 消息发布成功 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, MessageId: {properties?.MessageId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RabbitMQMessageQueueService] 消息发布失败 - ExchangeName: {exchangeName}, RoutingKey: {routingKey}, MessageId: {properties?.MessageId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Consume messages from queue / 从队列消费消息
        /// </summary>
        public async Task ConsumeAsync(string queueName, Func<byte[], MessageProperties, Task<bool>> handler, bool autoAck = false)
        {
            await EnsureChannelAsync().ConfigureAwait(false);

            if (_consumers.ContainsKey(queueName))
            {
                _logger.LogWarning($"Consumer for queue {queueName} already exists");
                return;
            }

            // Use AsyncEventingBasicConsumer for RabbitMQ.Client 7.0+
            // 使用 AsyncEventingBasicConsumer 支持 RabbitMQ.Client 7.0+
            var consumer = new AsyncEventingBasicConsumer(_channel);
            _consumers[queueName] = consumer;

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

            var consumerTag = await _channel.BasicConsumeAsync(queueName, autoAck, consumer);
            _logger.LogWarning($"[RabbitMQMessageQueueService] 消费者已启动 - QueueName: {queueName}, ConsumerTag: {consumerTag}, AutoAck: {autoAck}");
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

